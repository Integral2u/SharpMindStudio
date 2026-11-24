namespace SharpMind.Studio.Core.Runs;

public enum RunRetentionPolicy
{
    FastestPerModel,
    LastPerModel,
}

/// <summary>Groups model-bench runs by model and selects one to keep.</summary>
public sealed record RunRetentionPlan(
    string ModelKey,
    RunSummary Keep,
    IReadOnlyList<RunSummary> Delete);

public static class RunRetention
{
    public static IReadOnlyList<RunRetentionPlan> Plan(
        IEnumerable<RunSummary> runs,
        RunRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var plans = new List<RunRetentionPlan>();
        foreach (IGrouping<string, RunSummary> group in runs.GroupBy(
                     ModelKey, StringComparer.OrdinalIgnoreCase))
        {
            RunSummary[] ordered = policy == RunRetentionPolicy.FastestPerModel
                ? [.. group
                    .OrderByDescending(run => run.TokensPerSecond)
                    .ThenByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue)
                    .ThenByDescending(run => run.RunId, StringComparer.Ordinal)]
                : [.. group
                    .OrderByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue)
                    .ThenByDescending(run => run.RunId, StringComparer.Ordinal)];

            RunSummary keep = ordered[0];
            plans.Add(new RunRetentionPlan(
                group.Key,
                keep,
                [.. ordered.Skip(1)]));
        }

        return plans;
    }

    public static string ModelKey(RunSummary run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!string.IsNullOrWhiteSpace(run.ModelPath)) return run.ModelPath.Trim();
        if (!string.IsNullOrWhiteSpace(run.Subject)) return run.Subject.Trim();
        return run.RunId;
    }
}
