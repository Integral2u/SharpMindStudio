using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// The honest comparison behind a promotion: a candidate's held-out exam score
/// next to a baseline's, plus a verdict. The report is written as JSON for the
/// app and Markdown for a person, and neither model is modified.
/// </summary>
public sealed class TrainingPromotionReport
{
    public const string Better = "better";
    public const string Worse = "worse";
    public const string Inconclusive = "inconclusive";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string CandidateName { get; set; } = "";
    public string? CandidateModelPath { get; set; }
    public string? CandidateRunPath { get; set; }
    public double CandidateExamLoss { get; set; }
    public double CandidateExamPerplexity { get; set; }
    public double? CandidateExamBitsPerByte { get; set; }

    public string? BaselineName { get; set; }
    public string? BaselineModelPath { get; set; }
    public string? BaselineRunPath { get; set; }
    public double BaselineExamLoss { get; set; }
    public double BaselineExamPerplexity { get; set; }
    public double? BaselineExamBitsPerByte { get; set; }

    /// <summary>better / worse / inconclusive; a report can record all three.</summary>
    public string Verdict { get; set; } = Inconclusive;

    /// <summary>Candidate loss relative to the baseline, in percent (negative is better).</summary>
    public double DeltaPercent { get; set; }

    public string Summary { get; set; } = "";

    /// <summary>
    /// Builds the candidate/baseline report. Lower bits-per-byte wins when both
    /// runs have it, because it stays comparable after a tokenizer change; raw
    /// exam loss is the fallback. A change within half a percent is called
    /// inconclusive rather than overclaiming a difference the exam cannot resolve.
    /// </summary>
    public static TrainingPromotionReport Compare(
        TrainingRunRecord candidate,
        TrainingRunRecord baseline,
        double threshold = 0.005,
        string? candidateRunPath = null,
        string? baselineRunPath = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        if (candidate.ExamLoss is not { } candidateLoss || !double.IsFinite(candidateLoss))
            throw new InvalidOperationException($"'{candidate.Name}' has no held-out exam result.");
        if (baseline.ExamLoss is not { } baselineLoss || !double.IsFinite(baselineLoss) || baselineLoss <= 0)
            throw new InvalidOperationException($"'{baseline.Name}' has no usable held-out exam result.");

        bool useBitsPerByte =
            candidate.ExamBitsPerByte is { } candidateBpb
            && double.IsFinite(candidateBpb) && candidateBpb > 0
            && baseline.ExamBitsPerByte is { } baselineBpb
            && double.IsFinite(baselineBpb) && baselineBpb > 0;

        double candidateScore = useBitsPerByte ? candidate.ExamBitsPerByte!.Value : candidateLoss;
        double baselineScore = useBitsPerByte ? baseline.ExamBitsPerByte!.Value : baselineLoss;
        string metric = useBitsPerByte ? "bits/byte" : "loss";

        double delta = (candidateScore - baselineScore) / baselineScore;
        string verdict = delta < -threshold ? Better
            : delta > threshold ? Worse
            : Inconclusive;

        string summary = verdict switch
        {
            Better => $"{candidate.Name} beats {baseline.Name} on the held-out exam "
                      + $"({candidateScore:0.0000} vs {baselineScore:0.0000} {metric}, {delta:P2}).",
            Worse => $"{candidate.Name} trails {baseline.Name} on the held-out exam "
                     + $"({candidateScore:0.0000} vs {baselineScore:0.0000} {metric}, {delta:P2}).",
            _ => $"{candidate.Name} and {baseline.Name} are within the exam's resolution "
                 + $"({candidateScore:0.0000} vs {baselineScore:0.0000} {metric}, {delta:P2}).",
        };

        return new TrainingPromotionReport
        {
            CandidateName = candidate.Name,
            CandidateModelPath = candidate.ExportPath,
            CandidateRunPath = candidateRunPath,
            CandidateExamLoss = candidateLoss,
            CandidateExamPerplexity = candidate.ExamPerplexity ?? double.NaN,
            CandidateExamBitsPerByte = candidate.ExamBitsPerByte,
            BaselineName = baseline.Name,
            BaselineModelPath = baseline.ExportPath,
            BaselineRunPath = baselineRunPath,
            BaselineExamLoss = baselineLoss,
            BaselineExamPerplexity = baseline.ExamPerplexity ?? double.NaN,
            BaselineExamBitsPerByte = baseline.ExamBitsPerByte,
            Verdict = verdict,
            DeltaPercent = delta * 100.0,
            Summary = summary,
        };
    }

    public bool IsPromotion => Verdict == Better;
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

        return $"""
            # Promotion report: {report.CandidateName}

            {report.Summary}

            | | Candidate | Baseline |
            |---|---|---|
            | Model | `{report.CandidateModelPath ?? "(none)"}` | `{report.BaselineModelPath ?? "(none)"}` |
            | Exam loss | {report.CandidateExamLoss:0.0000} | {report.BaselineExamLoss:0.0000} |
            | Exam perplexity | {report.CandidateExamPerplexity:0.00} | {report.BaselineExamPerplexity:0.00} |
            | Exam bits/byte | {report.CandidateExamBitsPerByte:0.0000} | {report.BaselineExamBitsPerByte:0.0000} |
            | Delta | {report.DeltaPercent:+0.00;-0.00;0.00}% | |

            **Verdict:** {report.Verdict}

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
