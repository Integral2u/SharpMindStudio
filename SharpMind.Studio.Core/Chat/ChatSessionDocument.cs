using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Core.Chat;

public sealed record ChatSessionDocument
{
    public const string CurrentSchema = "studio-chat/1";

    public string Schema { get; init; } = CurrentSchema;
    public required string Id { get; init; }
    public string Name { get; set; } = "New chat";
    public required string ModelPath { get; init; }
    public string Provider { get; set; } = "local";
    public string? RemoteModel { get; set; }
    /// <summary>Compute provider id when <see cref="Provider"/> is "compute".</summary>
    public string? RemoteProvider { get; set; }
    public string? ModelFingerprint { get; init; }
    public string? FamilyId { get; init; }
    public string? FamilyName { get; init; }
    public string? InferenceVersion { get; init; }
    public string? TunePreset { get; init; }
    public string? TuneTarget { get; init; }
    public string Mode { get; init; } = "chat";
    public string? SystemPrompt { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
    public InferenceOverrides Overrides { get; set; } = new();
    public double? LastTokensPerSecond { get; set; }
    public decimal? LastCost { get; set; }
    public int? LastPromptTokens { get; set; }
    public int? LastCompletionTokens { get; set; }
    public int? LastToolCalls { get; set; }
}

public sealed record ChatSessionSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ModelPath { get; init; }
    public string Provider { get; init; } = "local";
    public string? RemoteModel { get; init; }
    public string Mode { get; init; } = "chat";
    public string? TunePreset { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public int MessageCount { get; init; }
    public string Preview { get; init; } = "";
}





