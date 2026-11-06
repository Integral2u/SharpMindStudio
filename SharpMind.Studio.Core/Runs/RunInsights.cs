using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Core.Runs;

public enum ToolCapability
{
    Untested,
    Passed,
    Failed,
}

/// <summary>What Studio knows about a model's ability to call tools.</summary>
public sealed record ToolCapabilityState(
    ToolCapability Capability,
    RunSummary? LatestRun,
    TuneMeasurement? Measurement)
{
    public string Label => Capability switch
    {
        ToolCapability.Passed => "tools ok",
        ToolCapability.Failed => "tools failed",
        _ => "tools untested",
    };

    public string CssClass => Capability switch
    {
        ToolCapability.Passed => "ok",
        ToolCapability.Failed => "failed",
        _ => "unknown",
    };
}

/// <summary>The fastest measured runs with and without a successful tool probe.</summary>
public sealed record RunHighlights(string? FastestWithToolsRunId, string? FastestWithoutToolsRunId);

public static class RunInsights
{
    public static ToolCapabilityState ResolveToolCapability(
        string? modelPath,
        string? fileName,
        IEnumerable<RunSummary> runs,
        TuneMeasurement? workMeasurement = null)
    {
        RunSummary? latestBench = LatestBench(runs, modelPath, fileName);

        if (workMeasurement is { Succeeded: true })
        {
            return new ToolCapabilityState(
                workMeasurement.ToolProbePassed ? ToolCapability.Passed : ToolCapability.Failed,
                latestBench,
                workMeasurement);
        }

        RunSummary? probe = runs
            .Where(run => string.Equals(run.Kind, "tool-probe", StringComparison.OrdinalIgnoreCase))
            .Where(run => Matches(run, modelPath, fileName))
            .OrderByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        ToolCapability capability = probe?.ToolStatus switch
        {
            "Yes" => ToolCapability.Passed,
            "No" => ToolCapability.Failed,
            _ => ToolCapability.Untested,
        };

        return new ToolCapabilityState(capability, latestBench, null);
    }

    public static RunHighlights FindHighlights(IEnumerable<RunSummary> runs)
    {
        List<RunSummary> measured = [.. runs.Where(run =>
            string.Equals(run.Kind, "model-bench", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(run.Status, "succeeded", StringComparison.OrdinalIgnoreCase) &&
            run.TokensPerSecond > 0)];

        RunSummary? withTools = measured
            .Where(run => string.Equals(run.ToolStatus, "Yes", StringComparison.OrdinalIgnoreCase))
            .MaxBy(run => run.TokensPerSecond);
        RunSummary? withoutTools = measured
            .Where(run => !string.Equals(run.ToolStatus, "Yes", StringComparison.OrdinalIgnoreCase))
            .MaxBy(run => run.TokensPerSecond);

        return new RunHighlights(withTools?.RunId, withoutTools?.RunId);
    }

    public static RunSummary? LatestBench(
        IEnumerable<RunSummary> runs,
        string? modelPath,
        string? fileName) =>
        runs
            .Where(run => string.Equals(run.Kind, "model-bench", StringComparison.OrdinalIgnoreCase))
            .Where(run => Matches(run, modelPath, fileName))
            .OrderByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static bool Matches(RunSummary run, string? modelPath, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(modelPath) &&
            !string.IsNullOrWhiteSpace(run.ModelPath) &&
            run.ModelPath.Equals(modelPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(fileName)) return false;
        string subject = run.Subject ?? "";
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return subject.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
               subject.Equals(stem, StringComparison.OrdinalIgnoreCase);
    }
}
