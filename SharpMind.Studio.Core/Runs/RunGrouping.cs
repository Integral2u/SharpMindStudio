namespace SharpMind.Studio.Core.Runs;

/// <summary>How the TESTS view buckets runs for reading.</summary>
public enum RunGroupMode
{
    Family,
    Model,
    RunType,
}

/// <summary>A named bucket of runs, already ordered fastest to slowest.</summary>
public sealed record RunGroup(string Key, string Title, IReadOnlyList<RunSummary> Runs)
{
    public RunSummary? Fastest => Runs.FirstOrDefault(run => run.TokensPerSecond > 0);

    public double BestTokensPerSecond => Fastest?.TokensPerSecond ?? 0;
}

public static class RunGrouping
{
    /// <summary>Runs with measured decode first, fastest to slowest, then the rest newest first.</summary>
    public static IEnumerable<RunSummary> FastestFirst(IEnumerable<RunSummary> runs) =>
        runs
            .OrderByDescending(run => run.TokensPerSecond > 0)
            .ThenByDescending(run => run.TokensPerSecond)
            .ThenByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(run => run.RunId, StringComparer.OrdinalIgnoreCase);

    public static RunSummary? FastestWithTools(IEnumerable<RunSummary> runs) =>
        Measured(runs)
            .Where(run => string.Equals(run.ToolStatus, "Yes", StringComparison.OrdinalIgnoreCase))
            .MaxBy(run => run.TokensPerSecond);

    public static RunSummary? FastestWithoutTools(IEnumerable<RunSummary> runs) =>
        Measured(runs)
            .Where(run => !string.Equals(run.ToolStatus, "Yes", StringComparison.OrdinalIgnoreCase))
            .MaxBy(run => run.TokensPerSecond);

    public static IReadOnlyList<RunGroup> Group(IEnumerable<RunSummary> runs, RunGroupMode mode)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        foreach (RunSummary run in runs)
        {
            (string key, string title) = Identify(run, mode);
            if (!buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket = new Bucket(key, title);
                buckets.Add(key, bucket);
            }

            bucket.Runs.Add(run);
        }

        return
        [
            .. buckets.Values
                .Select(bucket => new RunGroup(bucket.Key, bucket.Title, [.. FastestFirst(bucket.Runs)]))
                .OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        ];
    }

    public static (string Key, string Title) Identify(RunSummary run, RunGroupMode mode) => mode switch
    {
        RunGroupMode.Family => (
            string.IsNullOrWhiteSpace(run.FamilyId) ? "unknown" : run.FamilyId,
            string.IsNullOrWhiteSpace(run.FamilyName) ? "Unknown" : run.FamilyName),
        RunGroupMode.Model => ModelIdentity(run),
        _ => (
            string.IsNullOrWhiteSpace(run.Kind) ? "unknown" : run.Kind,
            string.IsNullOrWhiteSpace(run.Kind) ? "unknown" : run.Kind),
    };

    private static IEnumerable<RunSummary> Measured(IEnumerable<RunSummary> runs) =>
        runs.Where(run =>
            string.Equals(run.Kind, "model-bench", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(run.Status, "succeeded", StringComparison.OrdinalIgnoreCase) &&
            run.TokensPerSecond > 0);

    private static (string Key, string Title) ModelIdentity(RunSummary run)
    {
        string title = !string.IsNullOrWhiteSpace(run.ModelPath)
            ? Path.GetFileName(run.ModelPath)
            : !string.IsNullOrWhiteSpace(run.Subject)
                ? run.Subject
                : run.RunId;
        string key = !string.IsNullOrWhiteSpace(run.ModelPath) ? run.ModelPath : title;
        return (key, title);
    }

    private sealed class Bucket(string key, string title)
    {
        public string Key { get; } = key;
        public string Title { get; } = title;
        public List<RunSummary> Runs { get; } = [];
    }
}
