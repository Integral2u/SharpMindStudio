namespace SharpMind.Studio.Core.Runs;

/// <summary>The cheap dashboard view derived from a run's event stream.</summary>
public sealed record RunSummary
{
    public required string RunId { get; init; }
    public string? Kind { get; init; }
    public string? Subject { get; init; }
    public string? ModelPath { get; init; }
    public string FamilyId { get; init; } = "unknown";
    public string FamilyName { get; init; } = "Unknown";
    public string? Prompt { get; init; }
    public int? WarmupTokens { get; init; }
    public int? MeasureTokens { get; init; }
    public string? InferenceVersion { get; init; }
    public string? Device { get; init; }
    public string? Generator { get; init; }
    public string? Cache { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public string Status { get; init; } = "running";
    public int Steps { get; init; }
    public double TokensPerSecond { get; init; }
    public string? ProbeText { get; init; }
    public string ToolStatus { get; init; } = "N/A";
    public string? ToolRunId { get; init; }
    public string? Error { get; init; }

    public TimeSpan? Duration => StartedUtc is { } start && FinishedUtc is { } end ? end - start : null;
}


