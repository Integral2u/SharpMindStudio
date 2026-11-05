using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.ComputeHost;

public sealed record ChatCompletionMessage(string Role, string Content);

public sealed class ChatCompletionRequest
{
    public string? Model { get; set; }
    public List<ChatCompletionMessage> Messages { get; set; } = [];
    public bool Stream { get; set; }
    public float? Temperature { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>Newer OpenAI name for the same limit; used when <see cref="MaxTokens"/> is absent.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    public int? TokenLimit => MaxTokens ?? MaxCompletionTokens;
}

public sealed class TrainingJobRequest
{
    public required TrainingJobSettings Job { get; set; }
    public string? PluginsFolder { get; set; }
    public string? RecipeFolder { get; set; }
}

public sealed class ActivateModelRequest
{
    public string? Name { get; set; }
}

public sealed class TrainingJobState
{
    public required string Id { get; init; }
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public int? Step { get; set; }
    public double? Loss { get; set; }
    public double? ValLoss { get; set; }
    public string? ExportPath { get; set; }
    public string? AdapterPath { get; set; }
    public string? Error { get; set; }
    public bool? Success { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public List<string> Log { get; } = [];
}
