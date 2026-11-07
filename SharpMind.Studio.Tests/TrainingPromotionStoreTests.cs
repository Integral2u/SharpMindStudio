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
