using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Runs;

/// <summary>
/// Append-only run stream. The UI, history and headless output all derive from
/// these events; nothing else needs to be the source of truth for a run.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStarted), "runStarted")]
[JsonDerivedType(typeof(StepLogged), "stepLogged")]
[JsonDerivedType(typeof(EvalCompleted), "evalCompleted")]
[JsonDerivedType(typeof(ExpertStats), "expertStats")]
[JsonDerivedType(typeof(CheckpointSaved), "checkpointSaved")]
[JsonDerivedType(typeof(ProbeAnswered), "probeAnswered")]
[JsonDerivedType(typeof(ToolProbeCompleted), "toolProbeCompleted")]
[JsonDerivedType(typeof(RunFinished), "runFinished")]
public abstract record RunEvent
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RunStarted : RunEvent
{
    public required string RunId { get; init; }
    public required string Kind { get; init; }
    public required string Subject { get; init; }
    public string? ModelPath { get; init; }
    public string? Prompt { get; init; }
    public int? WarmupTokens { get; init; }
    public int? MeasureTokens { get; init; }
    public string? InferenceVersion { get; init; }
    public string? RecipeHash { get; init; }
    public string? BaseModelHash { get; init; }
    public string? Generator { get; init; }
    public string? Cache { get; init; }
    public required string Device { get; init; }
}

public sealed record StepLogged : RunEvent
{
    public required int Step { get; init; }
    public float? Loss { get; init; }
    public float? LearningRate { get; init; }
    public float? GradNorm { get; init; }
    public int TokensThisStep { get; init; }
    public long TokensSeen { get; init; }
    public float TokensPerSecond { get; init; }
    public long? MemoryBytes { get; init; }
}

public sealed record EvalCompleted : RunEvent
{
    public required int Step { get; init; }
    public required float ValLoss { get; init; }
    public required float Perplexity { get; init; }
}

public sealed record ExpertStats : RunEvent
{
    public required int Step { get; init; }
    public required int Layer { get; init; }
    public required int[] Counts { get; init; }
    public required float RoutingEntropy { get; init; }
}

public sealed record CheckpointSaved : RunEvent
{
    public required int Step { get; init; }
    public required string Path { get; init; }
    public string? ModelHash { get; init; }
}

public sealed record ProbeAnswered : RunEvent
{
    public required int Step { get; init; }
    public required string ProbeId { get; init; }
    public required string Text { get; init; }
}

public sealed record ToolProbeCompleted : RunEvent
{
    public required string Status { get; init; }
    public required string Detail { get; init; }
    public string? ToolRunId { get; init; }
}

public sealed record RunFinished : RunEvent
{
    public required string Status { get; init; }
    public string? ExportPath { get; init; }
    public required double DurationMs { get; init; }
    public string? Error { get; init; }
}


