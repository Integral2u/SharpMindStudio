namespace SharpMind.Studio.Core.Inference;

public sealed record TuneMeasurement
{
    public bool Succeeded { get; init; }
    public bool ToolProbePassed { get; init; }
    public double LoadMs { get; init; }
    public double TtftMs { get; init; }
    public double TokensPerSecond { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset MeasuredUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TuneProfile
{
    public TuneTarget Target { get; init; }
    public TunePreset Preset { get; init; }
    public InferenceOverrides Overrides { get; init; } = new();
    public TuneEstimate Estimate { get; init; } = new(0, 0, 0, 0, 0, false, "Full");
    public TuneMeasurement? Measurement { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public DateTimeOffset TunedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TuneProfileSet
{
    public required string ModelFingerprint { get; init; }
    public DateTimeOffset TunedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<TuneProfile> Profiles { get; init; } = [];

    public TuneProfile? Find(TuneTarget target, TunePreset preset) =>
        Profiles.FirstOrDefault(profile => profile.Target == target && profile.Preset == preset);

    public IReadOnlyList<TuneProfile> For(TuneTarget target) =>
        [.. Profiles.Where(profile => profile.Target == target)];

    public bool HasAll(TuneTarget target) =>
        Enum.GetValues<TunePreset>().All(preset => Find(target, preset) is not null);
}

public static class TuneFingerprint
{
    public static string ForModel(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }
}
