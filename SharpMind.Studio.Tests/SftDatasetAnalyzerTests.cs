using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SftDatasetAnalyzerTests
{
    [Fact]
    public async Task Analyze_CountsRecordsSplitsAndLayouts()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        var lines = new List<string>();
        for (int i = 0; i < 7; i++)
            lines.Add($$"""{"messages":[{"role":"user","content":"hi {{i}}"},{"role":"assistant","content":"hello {{i}}"}]}""");
        lines.Add("""{"conversations":[{"from":"human","value":"ping"},{"from":"gpt","value":"pong"}]}""");
        lines.Add("""{"prompt":"q","response":"a"}""");
        lines.Add("""{"instruction":"add","input":"1+1","output":"2"}""");
        lines.Add("not json");
        lines.Add("""{"prompt":"unanswered"}""");
        await File.WriteAllLinesAsync(path, lines);

        var job = Job(path, validationPercent: 20, testPercent: 20);
        SftDatasetPreview preview = await SftDatasetAnalyzer.AnalyzeAsync(
            job, temp.Combine("plugins"), baseModelPath: null);

        Assert.Equal(10, preview.Records);
        Assert.Equal(2, preview.Skipped);
        Assert.Equal(1, preview.Files);
        Assert.Equal(10, preview.TrainRecords + preview.ValidationRecords + preview.ExamRecords);
        Assert.Equal(7, preview.Layouts["messages"]);
        Assert.Equal(1, preview.Layouts["conversations"]);
        Assert.Equal(1, preview.Layouts["prompt/response"]);
        Assert.Equal(1, preview.Layouts["instruction/output"]);
        Assert.False(preview.Tokenized);
        Assert.True(preview.AverageTokens > 0);
        Assert.Equal(3, preview.Examples.Count);
        Assert.Contains("assistant:", preview.Examples[0].Rendered);
        Assert.Contains(preview.Warnings, warning => warning.Contains("Tokenizer unavailable"));
    }

    [Fact]
    public async Task Analyze_LimitReportsASampledScan()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllLinesAsync(path,
            Enumerable.Range(0, 10).Select(i =>
                $$"""{"prompt":"q{{i}}","response":"a{{i}}"}"""));

        SftDatasetPreview preview = await SftDatasetAnalyzer.AnalyzeAsync(
            Job(path, 0, 0), temp.Combine("plugins"), baseModelPath: null, maxRecords: 5);

        Assert.Equal(5, preview.Records);
        Assert.True(preview.LimitedScan);
    }

    [Fact]
    public async Task Analyze_EmptyDatasetWarnsAndDoesNotDivideByZero()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllTextAsync(path, """{"prompt":"unanswered"}""");

        SftDatasetPreview preview = await SftDatasetAnalyzer.AnalyzeAsync(
            Job(path, 10, 10), temp.Combine("plugins"), baseModelPath: null);

        Assert.Equal(0, preview.Records);
        Assert.Equal(1, preview.Skipped);
        Assert.Equal(0, preview.AverageTokens);
        Assert.Contains(preview.Warnings, warning => warning.Contains("No usable conversation records"));
    }

    private static TrainingJobSettings Job(string path, double validationPercent, double testPercent) => new()
    {
        Name = "sft-preview",
        SftMode = true,
        SftFormatter = "simple",
        Sources =
        [
            new JobSource
            {
                Component = new JobComponent
                {
                    DisplayName = "Chat JSONL",
                    TypeName = typeof(JsonlSource).FullName!,
                    Args = new Dictionary<string, string> { ["path"] = path },
                },
            },
        ],
        SeqLen = 64,
        ValidationPercent = validationPercent,
        TestPercent = testPercent,
    };
}
