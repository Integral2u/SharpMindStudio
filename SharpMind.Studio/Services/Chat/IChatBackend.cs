using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;

namespace SharpMind.Studio.Services.Chat;

/// <summary>
/// A provider-specific chat turn. The service owns the transcript, streaming
/// state and persistence; the backend owns transport, protocol parsing and any
/// provider tool loop. Adding a provider is one implementation plus a DI entry.
/// </summary>
public interface IChatBackend
{
    /// <summary>Stable provider key stored on the chat document.</summary>
    string Provider { get; }

    /// <summary>Null when the backend can run; otherwise a user-facing reason.</summary>
    string? Validate(ChatSessionDocument document);

    Task RunTurnAsync(ChatBackendContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a backend needs for one assistant turn.</summary>
public sealed class ChatBackendContext
{
    public required ChatSessionDocument Document { get; init; }

    /// <summary>The empty assistant row already appended to the transcript.</summary>
    public required ChatMessage Assistant { get; init; }

    public required string UserText { get; init; }

    /// <summary>
    /// The tool/agent builder for this turn. Work mode adds file, web and skill
    /// tools; chat still gets <c>delegate</c> and <c>spawn_agent</c>.
    /// </summary>
    public IAgentBuilder? Agent { get; init; }

    public required Action<string> SetStatus { get; init; }

    public required Action Notify { get; init; }
}
