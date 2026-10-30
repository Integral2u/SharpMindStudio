using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Tests;

public sealed class RunInsightsTests
{
    [Fact]
    public void FindHighlights_PicksFastestMeasuredRunWithAndWithoutTools()
    {
        RunSummary[] runs =
        [
            Bench("a", "m.gguf", 10, tool: "Yes"),
            Bench("b", "m.gguf", 28, tool: "N/A"),
            Bench("c", "m.gguf", 21, tool: "No"),
            Bench("failed", "m.gguf", 99, tool: "Yes", status: "failed"),
            Bench("probe", "m.gguf", 0, tool: "Yes", kind: "tool-probe"),
        ];

        RunHighlights highlights = RunInsights.FindHighlights(runs);

        Assert.Equal("a", highlights.FastestWithToolsRunId);
        Assert.Equal("b", highlights.FastestWithoutToolsRunId);
    }

    [Fact]
    public void FindHighlights_ReturnsNullWhenNothingWasMeasured()
    {
        RunSummary[] runs =
        [
            Bench("failed", "m.gguf", 0, status: "failed"),
            Bench("probe", "m.gguf", 0, kind: "tool-probe", tool: "Yes"),
        ];

        RunHighlights highlights = RunInsights.FindHighlights(runs);

        Assert.Null(highlights.FastestWithToolsRunId);
        Assert.Null(highlights.FastestWithoutToolsRunId);
    }

    [Fact]
    public void ResolveToolCapability_UsesTheLatestToolProbe()
    {
        RunSummary[] runs =
        [
            Bench("bench", "m.gguf", 12),
            Probe("old", "m.gguf", "No", DateTimeOffset.UtcNow.AddMinutes(-5)),
            Probe("new", "m.gguf", "Yes", DateTimeOffset.UtcNow),
        ];

        ToolCapabilityState state = RunInsights.ResolveToolCapability(
            @"C:\models\m.gguf", "m.gguf", runs);

        Assert.Equal(ToolCapability.Passed, state.Capability);
        Assert.Equal("bench", state.LatestRun?.RunId);
        Assert.Equal("tools ok", state.Label);
    }

    [Fact]
    public void ResolveToolCapability_ReportsUntestedWithoutAProbe()
    {
        RunSummary[] runs = [Bench("bench", "m.gguf", 12)];

        ToolCapabilityState state = RunInsights.ResolveToolCapability(null, "m.gguf", runs);

        Assert.Equal(ToolCapability.Untested, state.Capability);
        Assert.Equal("tools untested", state.Label);
    }

    [Fact]
    public void ResolveToolCapability_PrefersASucceededWorkMeasurement()
    {
        RunSummary[] runs = [Probe("probe", "m.gguf", "Yes", DateTimeOffset.UtcNow)];
        var failedMeasurement = new TuneMeasurement { Succeeded = true, ToolProbePassed = false };

        ToolCapabilityState state = RunInsights.ResolveToolCapability(
            null, "m.gguf", runs, failedMeasurement);

        Assert.Equal(ToolCapability.Failed, state.Capability);
        Assert.Same(failedMeasurement, state.Measurement);
    }

    [Fact]
    public void Group_ByFamily_PutsFastestFirstInsideEachFamily()
    {
        RunSummary[] runs =
        [
            Bench("qwen-fast", "qwen.gguf", 40, familyId: "qwen", familyName: "Qwen"),
            Bench("llama-slow", "llama.gguf", 8, familyId: "llama", familyName: "Llama"),
            Bench("llama-fast", "llama2.gguf", 32, familyId: "llama", familyName: "Llama"),
            Bench("llama-unmeasured", "llama.gguf", 0, familyId: "llama", familyName: "Llama",
                status: "failed"),
        ];

        IReadOnlyList<RunGroup> groups = RunGrouping.Group(runs, RunGroupMode.Family);

        Assert.Equal(["Llama", "Qwen"], groups.Select(group => group.Title));
        Assert.Equal(
            ["llama-fast", "llama-slow", "llama-unmeasured"],
            groups[0].Runs.Select(run => run.RunId));
        Assert.Equal("llama-fast", groups[0].Fastest?.RunId);
        Assert.Equal("qwen-fast", groups[1].Fastest?.RunId);
    }

    [Fact]
    public void Group_ByModel_UsesTheFileName()
    {
        RunSummary[] runs =
        [
            Bench("a", @"C:\models\tiny.gguf", 10),
            Bench("b", @"C:\models\large.gguf", 20),
            Bench("c", @"C:\models\tiny.gguf", 5),
        ];

        IReadOnlyList<RunGroup> groups = RunGrouping.Group(runs, RunGroupMode.Model);

        Assert.Equal(["large.gguf", "tiny.gguf"], groups.Select(group => group.Title));
        Assert.Equal(["a", "c"], groups[1].Runs.Select(run => run.RunId));
    }

    [Fact]
    public void Group_ByRunType_BucketsBenchmarksAndProbes()
    {
        RunSummary[] runs =
        [
            Bench("bench", "m.gguf", 10),
            Probe("probe", "m.gguf", "Yes", DateTimeOffset.UtcNow),
        ];

        IReadOnlyList<RunGroup> groups = RunGrouping.Group(runs, RunGroupMode.RunType);

        Assert.Equal(["model-bench", "tool-probe"], groups.Select(group => group.Title));
        Assert.Single(groups[0].Runs);
        Assert.Single(groups[1].Runs);
    }

    [Fact]
    public void FastestFirst_PutsMeasuredRunsBeforeUnmeasuredOnes()
    {
        RunSummary[] runs =
        [
            Bench("none", "m.gguf", 0),
            Bench("slow", "m.gguf", 4),
            Bench("fast", "m.gguf", 40),
        ];

        Assert.Equal(
            ["fast", "slow", "none"],
            RunGrouping.FastestFirst(runs).Select(run => run.RunId));
    }

    private static RunSummary Bench(
        string id,
        string subject,
        double tokensPerSecond,
        string tool = "N/A",
        string status = "succeeded",
        string kind = "model-bench",
        string familyId = "llama",
        string familyName = "Llama") =>
        new()
        {
            RunId = id,
            Kind = kind,
            Subject = subject,
            ModelPath = subject,
            FamilyId = familyId,
            FamilyName = familyName,
            Status = status,
            TokensPerSecond = tokensPerSecond,
            ToolStatus = tool,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

    private static RunSummary Probe(string id, string subject, string tool, DateTimeOffset at) =>
        new()
        {
            RunId = id,
            Kind = "tool-probe",
            Subject = subject,
            ModelPath = subject,
            FamilyId = "llama",
            FamilyName = "Llama",
            Status = "succeeded",
            ToolStatus = tool,
            StartedUtc = at,
        };
}
