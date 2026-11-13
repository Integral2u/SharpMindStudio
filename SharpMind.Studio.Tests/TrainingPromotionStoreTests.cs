using System.Text.Json;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The promotion report is the honest verdict: the candidate's exam score next
/// to the baseline's, written as JSON and Markdown. A difference inside the
/// exam's resolution is called inconclusive instead of overclaiming.
/// </summary>
public sealed class TrainingPromotionStoreTests
{
    [Fact]
    public void Compare_MarksABetterCandidateAndWritesBothFormats()
    {
        TrainingRunRecord candidate = Record("candidate", examLoss: 2.0, examPpl: 7.4);
        TrainingRunRecord baseline = Record("baseline", examLoss: 2.5, examPpl: 12.2);

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.Equal(TrainingPromotionReport.Better, report.Verdict);
        Assert.True(report.IsPromotion);
        Assert.Equal(-20.0, report.DeltaPercent, 3);
        Assert.Contains("beats", report.Summary);

        string folder = Path.Combine(Path.GetTempPath(), "sharpmind-promo-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = TrainingPromotionStore.Save(report, folder);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".md")));

            var saved = JsonSerializer.Deserialize<TrainingPromotionReport>(File.ReadAllText(path))!;
            Assert.Equal(TrainingPromotionReport.Better, saved.Verdict);
            Assert.Equal("candidate", saved.CandidateName);
            Assert.Equal(2.0, saved.CandidateExamLoss);
            Assert.Equal(2.5, saved.BaselineExamLoss);

            string markdown = File.ReadAllText(Path.ChangeExtension(path, ".md"));
            Assert.Contains("# Promotion report: candidate", markdown);
            Assert.Contains("**Verdict:** better", markdown);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Compare_CallsATinyDifferenceInconclusive()
    {
        TrainingPromotionReport report = TrainingPromotionReport.Compare(
            Record("candidate", examLoss: 2.501, examPpl: 12.2),
            Record("baseline", examLoss: 2.5, examPpl: 12.2));

        Assert.Equal(TrainingPromotionReport.Inconclusive, report.Verdict);
        Assert.False(report.IsPromotion);
    }

    [Fact]
    public void Compare_RequiresBothExamResults()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TrainingPromotionReport.Compare(
                Record("candidate", examLoss: null, examPpl: null),
                Record("baseline", examLoss: 2.5, examPpl: 12.2)));
    }

    [Fact]
    public void Compare_PrefersBitsPerByteWhenTokenizerChanged()
    {
        TrainingRunRecord candidate = Record(
            "candidate", examLoss: 3.0, examPpl: 20.1, examBpb: 1.0);
        TrainingRunRecord baseline = Record(
            "baseline", examLoss: 2.5, examPpl: 12.2, examBpb: 1.1);

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.Equal(TrainingPromotionReport.Better, report.Verdict);
        Assert.Contains("bits/byte", report.Summary);
        Assert.Equal(1.0, report.CandidateExamBitsPerByte!.Value, 4);
        Assert.Equal(1.1, report.BaselineExamBitsPerByte!.Value, 4);
    }

    [Fact]
    public void Compare_UsesAPairedBootstrapForPerDocumentScores()
    {
        TrainingRunRecord candidate = PairedRun("candidate", loss: 0.99);
        TrainingRunRecord baseline = PairedRun("baseline", loss: 1.0);

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.True(report.UsedBootstrap);
        Assert.Equal(200, report.PairedExamDocuments);
        Assert.Equal(TrainingPromotionReport.DefaultBootstrapSamples, report.BootstrapSamples);
        Assert.Equal(TrainingPromotionReport.Better, report.Verdict);
        Assert.True(report.ConfidenceLowPercent < -0.5);
        Assert.True(report.ConfidenceHighPercent < -0.5);
        Assert.Contains("95% CI", report.Summary);
    }

    [Fact]
    public void Compare_CallsAPairedBootstrapWithNoisyScoresInconclusive()
    {
        TrainingRunRecord candidate = NoisyRun("candidate", flip: true);
        TrainingRunRecord baseline = NoisyRun("baseline", flip: false);

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.True(report.UsedBootstrap);
        Assert.Equal(TrainingPromotionReport.Inconclusive, report.Verdict);
        Assert.True(report.ConfidenceLowPercent < 0);
        Assert.True(report.ConfidenceHighPercent > 0);
    }

    [Fact]
    public void Compare_RefusesABootstrapBelowTheMinimumExamSize()
    {
        TrainingRunRecord candidate = PairedRun("candidate", loss: 0.99, count: 10);
        TrainingRunRecord baseline = PairedRun("baseline", loss: 1.0, count: 10);

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.False(report.UsedBootstrap);
        Assert.Equal(TrainingPromotionReport.Inconclusive, report.Verdict);
        Assert.Equal(10, report.PairedExamDocuments);
        Assert.Contains("too small", report.Summary);
    }

    [Fact]
    public void Compare_RefusesToPairDifferentExamDocuments()
    {
        TrainingRunRecord candidate = PairedRun("candidate", loss: 0.99, keyPrefix: "candidate");
        TrainingRunRecord baseline = PairedRun("baseline", loss: 1.0, keyPrefix: "baseline");

        TrainingPromotionReport report = TrainingPromotionReport.Compare(candidate, baseline);

        Assert.False(report.UsedBootstrap);
        Assert.Equal(0, report.PairedExamDocuments);
        Assert.Equal(TrainingPromotionReport.Inconclusive, report.Verdict);
    }

    [Fact]
    public void BitsPerByte_NormalisesLossByTokensAndBytes()
    {
        Assert.Equal(1.0, ExamMetrics.BitsPerByte(Math.Log(2), tokens: 1, bytes: 1), 6);
        Assert.Equal(
            2.0,
            ExamMetrics.BitsPerByte(Math.Log(2) * 2, tokens: 1, bytes: 1),
            6);
        Assert.True(double.IsNaN(ExamMetrics.BitsPerByte(1.0, tokens: 0, bytes: 1)));
        Assert.True(double.IsNaN(ExamMetrics.BitsPerByte(1.0, tokens: 1, bytes: 0)));
    }

    private static TrainingRunRecord PairedRun(
        string name,
        double loss,
        int count = 200,
        string keyPrefix = "doc")
    {
        var docs = Enumerable.Range(0, count)
            .Select(i => new ExamDocumentScore($"{keyPrefix}-{i}", loss, 100, 400))
            .ToList();
        return new TrainingRunRecord
        {
            Name = name,
            Success = true,
            ExportPath = $@"C:\models\{name}.smm",
            ExamLoss = loss,
            ExamPerplexity = Math.Exp(loss),
            ExamDocumentScores = docs,
            ExamEvaluatedUtc = DateTime.UtcNow,
        };
    }

    private static TrainingRunRecord NoisyRun(string name, bool flip, int count = 200)
    {
        var docs = Enumerable.Range(0, count)
            .Select(i =>
            {
                double baseLoss = 1.0 + 0.1 * (i % 2 == 0 ? 1 : -1);
                double loss = flip ? 2.0 - baseLoss : baseLoss;
                return new ExamDocumentScore($"doc-{i}", loss, 100, 400);
            })
            .ToList();
        double average = docs.Average(score => score.Loss);
        return new TrainingRunRecord
        {
            Name = name,
            Success = true,
            ExportPath = $@"C:\models\{name}.smm",
            ExamLoss = average,
            ExamPerplexity = Math.Exp(average),
            ExamDocumentScores = docs,
            ExamEvaluatedUtc = DateTime.UtcNow,
        };
    }

    private static TrainingRunRecord Record(
        string name,
        double? examLoss,
        double? examPpl,
        double? examBpb = null) => new()
    {
        Name = name,
        Success = true,
        ExportPath = $@"C:\models\{name}.smm",
        ExamLoss = examLoss,
        ExamPerplexity = examPpl,
        ExamBitsPerByte = examBpb,
        ExamEvaluatedUtc = examLoss is null ? null : DateTime.UtcNow,
    };
}
