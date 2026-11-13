using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Tests;

public sealed class RunGroupingTests
{
    [Fact]
    public void WithoutLinkedToolProbes_FoldsAProbeIntoItsBenchmarkRow()
    {
        RunSummary[] runs =
        [
            new() { RunId = "bench", Kind = "model-bench", ToolStatus = "Yes", ToolRunId = "probe" },
            new() { RunId = "probe", Kind = "tool-probe", ToolStatus = "N/A" },
            new() { RunId = "standalone", Kind = "tool-probe", ToolStatus = "Yes" },
        ];

        IReadOnlyList<RunSummary> visible = RunGrouping.WithoutLinkedToolProbes(runs);

        Assert.Equal(
            new[] { "bench", "standalone" },
            visible.Select(run => run.RunId));
    }

    [Fact]
    public void WithoutLinkedToolProbes_LeavesAnUnlinkedListAlone()
    {
        RunSummary[] runs =
        [
            new() { RunId = "a", Kind = "model-bench" },
            new() { RunId = "b", Kind = "model-bench" },
        ];

        IReadOnlyList<RunSummary> visible = RunGrouping.WithoutLinkedToolProbes(runs);

        Assert.Same(runs, visible);
    }

    [Fact]
    public void WithoutLinkedToolProbes_PairsAnUnlinkedProbeWithItsBenchmark()
    {
        DateTimeOffset start = new(2026, 11, 12, 17, 42, 0, TimeSpan.Zero);
        RunSummary[] runs =
        [
            new()
            {
                RunId = "bench",
                Kind = "model-bench",
                ModelPath = @"C:\models\a.gguf",
                StartedUtc = start,
                FinishedUtc = start.AddMinutes(2),
                ToolStatus = "N/A",
            },
            new()
            {
                RunId = "probe",
                Kind = "tool-probe",
                ModelPath = @"C:\models\a.gguf",
                StartedUtc = start.AddMinutes(2).AddSeconds(5),
                ToolStatus = "No",
            },
        ];

        IReadOnlyList<RunSummary> visible = RunGrouping.WithoutLinkedToolProbes(runs);

        RunSummary only = Assert.Single(visible);
        Assert.Equal("bench", only.RunId);
        Assert.Equal("No", only.ToolStatus);
        Assert.Equal("probe", only.ToolRunId);
    }

    [Fact]
    public void WithoutLinkedToolProbes_KeepsAProbeThatHasNoBenchmark()
    {
        DateTimeOffset start = new(2026, 11, 12, 17, 42, 0, TimeSpan.Zero);
        RunSummary[] runs =
        [
            new()
            {
                RunId = "bench",
                Kind = "model-bench",
                ModelPath = @"C:\models\a.gguf",
                StartedUtc = start,
                FinishedUtc = start.AddMinutes(2),
            },
            new()
            {
                RunId = "probe",
                Kind = "tool-probe",
                ModelPath = @"C:\models\b.gguf",
                StartedUtc = start.AddMinutes(2).AddSeconds(5),
                ToolStatus = "Yes",
            },
        ];

        IReadOnlyList<RunSummary> visible = RunGrouping.WithoutLinkedToolProbes(runs);

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, run => run.RunId == "probe");
    }
}
