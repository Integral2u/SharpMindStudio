using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// The honest comparison behind a promotion: a candidate's held-out exam score
/// next to a baseline's, plus a verdict. When both runs recorded per-document
/// exam losses, the verdict comes from a paired bootstrap over those documents;
/// older runs fall back to the aggregate exam score. The report is written as
/// JSON for the app and Markdown for a person, and neither model is modified.
/// </summary>
public sealed class TrainingPromotionReport
{
    public const string Better = "better";
    public const string Worse = "worse";
    public const string Inconclusive = "inconclusive";

    /// <summary>Minimum paired documents before a bootstrap verdict is allowed.</summary>
    public const int MinimumDocuments = 100;

    /// <summary>Minimum scored tokens before a bootstrap verdict is allowed.</summary>
    public const long MinimumTokens = 10_000;

    public const int DefaultBootstrapSamples = 2000;
    public const int DefaultBootstrapSeed = 20261114;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string CandidateName { get; set; } = "";
    public string? CandidateModelPath { get; set; }
    public string? CandidateRunPath { get; set; }
    public double CandidateExamLoss { get; set; }
    public double CandidateExamPerplexity { get; set; }
    public double? CandidateExamBitsPerByte { get; set; }
    public int CandidateExamDocuments { get; set; }

    public string? BaselineName { get; set; }
    public string? BaselineModelPath { get; set; }
    public string? BaselineRunPath { get; set; }
    public double BaselineExamLoss { get; set; }
    public double BaselineExamPerplexity { get; set; }
    public double? BaselineExamBitsPerByte { get; set; }
    public int BaselineExamDocuments { get; set; }

    /// <summary>better / worse / inconclusive; a report can record all three.</summary>
    public string Verdict { get; set; } = Inconclusive;

    /// <summary>Candidate loss relative to the baseline, in percent (negative is better).</summary>
    public double DeltaPercent { get; set; }

    /// <summary>How many exam documents the candidate and baseline both scored.</summary>
    public int PairedExamDocuments { get; set; }

    /// <summary>Lower bound of the 95% bootstrap interval, in percent.</summary>
    public double? ConfidenceLowPercent { get; set; }

    /// <summary>Upper bound of the 95% bootstrap interval, in percent.</summary>
    public double? ConfidenceHighPercent { get; set; }

    public int BootstrapSamples { get; set; }
    public int BootstrapSeed { get; set; }
    public bool UsedBootstrap { get; set; }

    public string? StatisticsNote { get; set; }
    public string Summary { get; set; } = "";

    /// <summary>
    /// Builds the candidate/baseline report. Lower bits-per-byte wins when both
    /// runs have it, because it stays comparable after a tokenizer change; raw
    /// exam loss is the fallback. A paired bootstrap over the shared exam
    /// documents gives a 95% interval; a change is called better or worse only
    /// when the interval clears both zero and the practical threshold.
    /// </summary>
    public static TrainingPromotionReport Compare(
        TrainingRunRecord candidate,
        TrainingRunRecord baseline,
        double threshold = 0.005,
        string? candidateRunPath = null,
        string? baselineRunPath = null,
        int bootstrapSamples = DefaultBootstrapSamples,
        int bootstrapSeed = DefaultBootstrapSeed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        if (candidate.ExamLoss is not { } candidateLoss || !double.IsFinite(candidateLoss))
            throw new InvalidOperationException($"'{candidate.Name}' has no held-out exam result.");
        if (baseline.ExamLoss is not { } baselineLoss || !double.IsFinite(baselineLoss) || baselineLoss <= 0)
            throw new InvalidOperationException($"'{baseline.Name}' has no usable held-out exam result.");

        List<ExamDocumentScore> candidateDocs = candidate.ExamDocumentScores ?? [];
        List<ExamDocumentScore> baselineDocs = baseline.ExamDocumentScores ?? [];

        string verdict;
        string summary;
        string? note;
        double delta;
        int pairedDocuments = 0;
        double? lowPercent = null;
        double? highPercent = null;
        bool usedBootstrap = false;

        if (candidateDocs.Count > 0 && baselineDocs.Count > 0)
        {
            var baselineByKey = baselineDocs
                .GroupBy(score => score.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var pairs = new List<(ExamDocumentScore Candidate, ExamDocumentScore Baseline)>();
            foreach (ExamDocumentScore candidateScore in candidateDocs)
            {
                if (baselineByKey.TryGetValue(candidateScore.Key, out ExamDocumentScore? baselineScore))
                    pairs.Add((candidateScore, baselineScore));
            }

            pairedDocuments = pairs.Count;
            long pairedTokens = pairs.Sum(pair =>
                Math.Min(pair.Candidate.Tokens, pair.Baseline.Tokens));

            if (pairs.Count == 0)
            {
                verdict = Inconclusive;
                delta = 0;
                summary = "The two runs were scored on different exam documents, so they "
                    + "cannot be paired for a promotion verdict.";
                note = "Re-run both exams from the same job and seed so they share the held-out slice.";
            }
            else if (pairs.Count < MinimumDocuments || pairedTokens < MinimumTokens)
            {
                verdict = Inconclusive;
                delta = 0;
                summary = $"The held-out exam is too small for a defensible verdict "
                    + $"({pairs.Count:N0} paired document(s), {pairedTokens:N0} tokens; at least "
                    + $"{MinimumDocuments:N0} documents and {MinimumTokens:N0} tokens are needed).";
                note = "Add exam data or re-run the exam with more held-out documents.";
            }
            else
            {
                bool useBitsPerByte = pairs.All(pair =>
                    ValidBitsPerByte(pair.Candidate) && ValidBitsPerByte(pair.Baseline));
                double[] candidateScores = [.. pairs.Select(pair => Score(pair.Candidate, useBitsPerByte))];
                double[] baselineScores = [.. pairs.Select(pair => Score(pair.Baseline, useBitsPerByte))];

                if (useBitsPerByte &&
                    (candidateScores.Any(value => !double.IsFinite(value) || value <= 0) ||
                     baselineScores.Any(value => !double.IsFinite(value) || value <= 0)))
                {
                    useBitsPerByte = false;
                }

                if (!useBitsPerByte)
                {
                    candidateScores = [.. pairs.Select(pair => pair.Candidate.Loss)];
                    baselineScores = [.. pairs.Select(pair => pair.Baseline.Loss)];
                }

                double meanCandidate = candidateScores.Average();
                double meanBaseline = baselineScores.Average();
                if (!(meanBaseline > 0))
                {
                    useBitsPerByte = false;
                    candidateScores = [.. pairs.Select(pair => pair.Candidate.Loss)];
                    baselineScores = [.. pairs.Select(pair => pair.Baseline.Loss)];
                    meanCandidate = candidateScores.Average();
                    meanBaseline = baselineScores.Average();
                }

                delta = meanBaseline > 0
                    ? (meanCandidate - meanBaseline) / meanBaseline
                    : 0;

                var resampled = new double[bootstrapSamples];
                var random = new Random(bootstrapSeed);
                for (int sample = 0; sample < resampled.Length; sample++)
                {
                    double sumCandidate = 0;
                    double sumBaseline = 0;
                    for (int i = 0; i < candidateScores.Length; i++)
                    {
                        int index = random.Next(candidateScores.Length);
                        sumCandidate += candidateScores[index];
                        sumBaseline += baselineScores[index];
                    }

                    double sampleBaseline = sumBaseline / candidateScores.Length;
                    resampled[sample] = sampleBaseline > 0
                        ? (sumCandidate / candidateScores.Length - sampleBaseline) / sampleBaseline
                        : 0;
                }

                Array.Sort(resampled);
                double low = Percentile(resampled, 0.025);
                double high = Percentile(resampled, 0.975);
                lowPercent = low * 100;
                highPercent = high * 100;
                usedBootstrap = true;

                string metric = useBitsPerByte ? "bits/byte" : "loss";
                string effect = $"{meanCandidate:0.0000} vs {meanBaseline:0.0000} {metric}, "
                    + $"{delta:P2}, 95% CI [{low:P2}, {high:P2}] over {pairs.Count:N0} documents";
                verdict = high < -threshold ? Better
                    : low > threshold ? Worse
                    : Inconclusive;
                summary = verdict switch
                {
                    Better => $"{candidate.Name} beats {baseline.Name} on the held-out exam ({effect}).",
                    Worse => $"{candidate.Name} trails {baseline.Name} on the held-out exam ({effect}).",
                    _ => $"{candidate.Name} and {baseline.Name} are within the exam's resolution "
                         + $"or the practical threshold ({effect}).",
                };
                note = $"Paired bootstrap, {bootstrapSamples:N0} resamples, seed {bootstrapSeed}.";
            }
        }
        else
        {
            // Older runs have only the aggregate exam score. Keep the previous
            // threshold behaviour, but say the result is not bootstrapped.
            bool useBitsPerByte =
                candidate.ExamBitsPerByte is { } candidateBpb
                && double.IsFinite(candidateBpb) && candidateBpb > 0
                && baseline.ExamBitsPerByte is { } baselineBpb
                && double.IsFinite(baselineBpb) && baselineBpb > 0;

            double candidateScore = useBitsPerByte ? candidate.ExamBitsPerByte!.Value : candidateLoss;
            double baselineScore = useBitsPerByte ? baseline.ExamBitsPerByte!.Value : baselineLoss;
            string metric = useBitsPerByte ? "bits/byte" : "loss";

            delta = (candidateScore - baselineScore) / baselineScore;
            verdict = delta < -threshold ? Better
                : delta > threshold ? Worse
                : Inconclusive;

            string aggregate = $"{candidateScore:0.0000} vs {baselineScore:0.0000} {metric}, {delta:P2}";
            summary = verdict switch
            {
                Better => $"{candidate.Name} beats {baseline.Name} on the held-out exam ({aggregate}).",
                Worse => $"{candidate.Name} trails {baseline.Name} on the held-out exam ({aggregate}).",
                _ => $"{candidate.Name} and {baseline.Name} are within the exam's resolution "
                     + $"({aggregate}).",
            };
            note = "Aggregate exam result only; re-run the exam to record per-document losses "
                + "for a paired bootstrap.";
        }

        return new TrainingPromotionReport
        {
            CandidateName = candidate.Name,
            CandidateModelPath = candidate.ExportPath,
            CandidateRunPath = candidateRunPath,
            CandidateExamLoss = candidateLoss,
            CandidateExamPerplexity = candidate.ExamPerplexity ?? double.NaN,
            CandidateExamBitsPerByte = candidate.ExamBitsPerByte,
            CandidateExamDocuments = candidateDocs.Count,
            BaselineName = baseline.Name,
            BaselineModelPath = baseline.ExportPath,
            BaselineRunPath = baselineRunPath,
            BaselineExamLoss = baselineLoss,
            BaselineExamPerplexity = baseline.ExamPerplexity ?? double.NaN,
            BaselineExamBitsPerByte = baseline.ExamBitsPerByte,
            BaselineExamDocuments = baselineDocs.Count,
            Verdict = verdict,
            DeltaPercent = delta * 100.0,
            PairedExamDocuments = pairedDocuments,
            ConfidenceLowPercent = lowPercent,
            ConfidenceHighPercent = highPercent,
            BootstrapSamples = usedBootstrap ? bootstrapSamples : 0,
            BootstrapSeed = usedBootstrap ? bootstrapSeed : 0,
            UsedBootstrap = usedBootstrap,
            StatisticsNote = note,
            Summary = summary,
        };
    }

    public bool IsPromotion => Verdict == Better;

    private static bool ValidBitsPerByte(ExamDocumentScore score) =>
        score.Tokens > 0 && score.Bytes > 0 && double.IsFinite(score.Loss);

    private static double Score(ExamDocumentScore score, bool useBitsPerByte) =>
        useBitsPerByte
            ? ExamMetrics.BitsPerByte(score.Loss, score.Tokens, score.Bytes)
            : score.Loss;

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return double.NaN;
        double position = (sorted.Length - 1) * fraction;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }
}

/// <summary>
/// Writes promotion reports under <c>Training/promotions</c> as JSON and a
/// human-readable Markdown summary. Best-effort: a report failure must not
/// change the models.
/// </summary>
public static class TrainingPromotionStore
{
    public static string Folder => Path.Combine(StudioPaths.Training, "promotions");

    public static string Save(TrainingPromotionReport report, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        folder ??= Folder;
        Directory.CreateDirectory(folder);

        string stem = $"{report.CreatedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{Sanitize(report.CandidateName)}";
        string jsonPath = Path.Combine(folder, stem + ".json");
        string markdownPath = Path.Combine(folder, stem + ".md");

        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(
            report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(markdownPath, ToMarkdown(report));
        return jsonPath;
    }

    public static string ToMarkdown(TrainingPromotionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        string interval = report.ConfidenceLowPercent is { } low
            && report.ConfidenceHighPercent is { } high
            ? $"{low:+0.00;-0.00;0.00}% to {high:+0.00;-0.00;0.00}%"
            : "not available";
        string bootstrap = report.UsedBootstrap
            ? $"{report.BootstrapSamples:N0} resamples (seed {report.BootstrapSeed})"
            : "aggregate only";

        return $"""
            # Promotion report: {report.CandidateName}

            {report.Summary}

            | | Candidate | Baseline |
            |---|---|---|
            | Model | `{report.CandidateModelPath ?? "(none)"}` | `{report.BaselineModelPath ?? "(none)"}` |
            | Exam loss | {report.CandidateExamLoss:0.0000} | {report.BaselineExamLoss:0.0000} |
            | Exam perplexity | {report.CandidateExamPerplexity:0.00} | {report.BaselineExamPerplexity:0.00} |
            | Exam bits/byte | {report.CandidateExamBitsPerByte:0.0000} | {report.BaselineExamBitsPerByte:0.0000} |
            | Exam documents | {report.CandidateExamDocuments} | {report.BaselineExamDocuments} |
            | Paired documents | {report.PairedExamDocuments} | |
            | Delta | {report.DeltaPercent:+0.00;-0.00;0.00}% | |
            | 95% CI | {interval} | |
            | Bootstrap | {bootstrap} | |

            **Verdict:** {report.Verdict}

            {report.StatisticsNote}

            Created {report.CreatedUtc:u}. The held-out exam slice was excluded from training
            and from checkpoint selection.
            """;
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "candidate" : name.Trim();
    }
}
