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

    /// <summary>
    /// Folds a tool-probe run into its benchmark row. Linked probes are already
    /// recorded on the benchmark through <see cref="RunSummary.ToolRunId"/>;
    /// older tune runs created the probe as a separate run, so those are paired
    /// by model and time. Either way the benchmark reports Yes/No/N/A in its
    /// Tool column and the duplicate line disappears. Probes without a parent
    /// stay visible.
    /// </summary>
    public static IReadOnlyList<RunSummary> WithoutLinkedToolProbes(IEnumerable<RunSummary> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        IReadOnlyList<RunSummary> list = runs as IReadOnlyList<RunSummary> ?? [.. runs];

        var linked = new HashSet<string>(
            list.Where(run => !string.IsNullOrWhiteSpace(run.ToolRunId))
                .Select(run => run.ToolRunId!),
            StringComparer.OrdinalIgnoreCase);

        List<RunSummary> benchmarks = [.. list.Where(IsBenchmark)];
        var merged = new Dictionary<string, RunSummary>(StringComparer.OrdinalIgnoreCase);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Newest probe first, so a retry wins over the attempt before it.
        foreach (RunSummary probe in list
            .Where(IsProbe)
            .Where(run => !linked.Contains(run.RunId))
            .OrderByDescending(run => run.StartedUtc ?? DateTimeOffset.MinValue))
        {
            RunSummary? bench = benchmarks
                .Where(run => !merged.ContainsKey(run.RunId))
                .Where(run => string.IsNullOrWhiteSpace(run.ToolRunId))
                .Where(run => SameModel(run, probe) && WithinWindow(run, probe))
                .OrderBy(run => Distance(run, probe))
                .FirstOrDefault();
            if (bench is null) continue;

            merged[bench.RunId] = bench with
            {
                ToolStatus = probe.ToolStatus,
                ToolRunId = probe.RunId,
            };
            folded.Add(probe.RunId);
        }

        if (linked.Count == 0 && merged.Count == 0) return list;

        return
        [
            .. list
                .Where(run => !linked.Contains(run.RunId) && !folded.Contains(run.RunId))
                .Select(run => merged.TryGetValue(run.RunId, out RunSummary? updated) ? updated : run)
        ];
    }

    private static bool IsBenchmark(RunSummary run) =>
        string.Equals(run.Kind, "model-bench", StringComparison.OrdinalIgnoreCase);

    private static bool IsProbe(RunSummary run) =>
        string.Equals(run.Kind, "tool-probe", StringComparison.OrdinalIgnoreCase);

    private static bool SameModel(RunSummary left, RunSummary right)
    {
        if (!string.IsNullOrWhiteSpace(left.ModelPath) &&
            !string.IsNullOrWhiteSpace(right.ModelPath))
        {
            return string.Equals(left.ModelPath, right.ModelPath, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(left.Subject) &&
               string.Equals(left.Subject, right.Subject, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WithinWindow(RunSummary bench, RunSummary probe)
    {
        DateTimeOffset start = bench.StartedUtc ?? DateTimeOffset.MinValue;
        DateTimeOffset end = bench.FinishedUtc is { } finished
            ? finished.AddMinutes(30)
            : start.AddHours(6);
        DateTimeOffset at = probe.StartedUtc ?? DateTimeOffset.MinValue;
        return at >= start.AddMinutes(-1) && at <= end;
    }

    private static long Distance(RunSummary bench, RunSummary probe)
    {
        DateTimeOffset start = bench.StartedUtc ?? DateTimeOffset.MinValue;
        DateTimeOffset at = probe.StartedUtc ?? DateTimeOffset.MinValue;
        return Math.Abs((at - start).Ticks);
    }

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
