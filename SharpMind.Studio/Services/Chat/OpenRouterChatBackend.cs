using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.OpenRouter;

namespace SharpMind.Studio.Services.Chat;

/// <summary>
/// OpenRouter chat completions, including the native tool-call loop. The service
/// supplies the assistant row and executes nothing itself; this backend calls
/// the Work agent and appends tool results to the transcript.
/// </summary>
public sealed class OpenRouterChatBackend(
    OpenRouterSettingsService settings,
    OpenRouterClient client) : IChatBackend
{
    private const int MaxToolCalls = 10;

    public string Provider => "openrouter";

    public string? Validate(ChatSessionDocument document)
    {
        if (!settings.Connection.IsUsable)
            return "OpenRouter is not configured. Add an API key in Settings.";

        return ResolveModel(document) is { Length: > 0 }
            ? null
            : "Choose an OpenRouter model id first.";
    }

    public async Task RunTurnAsync(
        ChatBackendContext context,
        CancellationToken cancellationToken)
    {
        ChatSessionDocument document = context.Document;
        OpenRouterConnection connection = settings.Connection;
        string model = ResolveModel(document) ?? "";

        IAgentBuilder? agent = context.Agent;
        if (agent is not null)
        {
            // OpenAI tool calling is native here; do not also teach a JSON call shape.
            agent.CallFormat = ToolCallFormat.None;
            agent.ToolListInPrompt = false;
            agent.WithCustomRule(
                "When a UI tool returns a choice, treat it as the person's answer and continue "
                + "from it. Do not repeat the option list unless they ask.");
        }

        IReadOnlyList<JsonObject>? tools = agent is null
            ? null
            : OpenRouterMessageBuilder.BuildTools(agent);

        List<JsonObject> messages = OpenRouterMessageBuilder.BuildMessages(
            [.. document.Messages.Take(document.Messages.Count - 1)],
            document.SystemPrompt,
            agent);

        int toolCallsUsed = 0;
        int? promptTokens = null;
        int? completionTokens = null;
        decimal? cost = null;
        double? lastTps = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ChatMessage assistant = context.Assistant;

        while (true)
        {
            var accumulators = new Dictionary<int, ToolCallAccumulator>();
            var lastNotify = TimeSpan.Zero;

            await foreach (OpenRouterStreamEvent entry in client.StreamAsync(
                new OpenRouterRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    Temperature = document.Overrides.Temperature,
                    TopP = document.Overrides.TopP,
                    TopK = document.Overrides.TopK,
                    RepetitionPenalty = document.Overrides.RepetitionPenalty,
                    MaxTokens = document.Overrides.MaxNewTokens,
                    Seed = document.Overrides.Seed,
                },
                connection,
                cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(entry.Error))
                    throw new OpenRouterException(entry.Error);

                if (entry.PromptTokens is int prompt) promptTokens = prompt;
                if (entry.CompletionTokens is int completion) completionTokens = completion;
                if (entry.Cost is decimal entryCost) cost = entryCost;

                if (entry.ToolCalls is { Count: > 0 })
                {
                    foreach (OpenRouterToolCallDelta delta in entry.ToolCalls)
                        AccumulateToolCall(accumulators, delta);
                }

                if (!string.IsNullOrEmpty(entry.Delta))
                {
                    assistant.Content += entry.Delta;
                    context.SetStatus("Writing...");
                    if (lastNotify == TimeSpan.Zero
                        || stopwatch.Elapsed - lastNotify > TimeSpan.FromMilliseconds(40))
                    {
                        lastNotify = stopwatch.Elapsed;
                        context.Notify();
                    }
                }
            }

            if (accumulators.Count == 0) break;

            if (toolCallsUsed >= MaxToolCalls)
            {
                assistant.Content += "\n\n[Tool-call limit reached for this turn.]";
                break;
            }

            List<OpenRouterCompletedToolCall> completed = [.. accumulators.Values
                .OrderBy(call => call.Index)
                .Select(call => call.ToCall())];
            JsonArray toolCallsJson = OpenRouterMessageBuilder.ToToolCallsJson(completed);

            AttachToolCalls(document.Messages, assistant, toolCallsJson);
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(assistant.Content) ? null : assistant.Content,
                ["tool_calls"] = toolCallsJson.DeepClone(),
            });

            foreach (OpenRouterCompletedToolCall call in completed)
            {
                toolCallsUsed++;
                context.SetStatus($"Running {call.Name}...");
                context.Notify();

                JsonObject arguments;
                try
                {
                    arguments = JsonNode.Parse(call.Arguments) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    arguments = new JsonObject();
                }

                JsonObject result = agent is null
                    ? new JsonObject
                    {
                        ["status"] = "error",
                        ["message"] = "Tools are only available in Work mode.",
                    }
                    : await Task.Run(
                        () => agent.CallToolAsync(
                            new JsonObject
                            {
                                ["tool"] = call.Name,
                                ["arguments"] = arguments,
                            },
                            cancellationToken),
                        cancellationToken).ConfigureAwait(false);

                string resultText = OpenRouterMessageBuilder.FormatToolResult(
                    call.Name, arguments, result);
                messages.Add(OpenRouterMessageBuilder.ToolResult(call.Id, resultText));
                document.Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = resultText,
                    Metadata = new Dictionary<string, string>
                    {
                        [OpenRouterMetadata.ToolCallId] = call.Id,
                    },
                });
            }

            assistant = ChatMessage.Agent("");
            document.Messages.Add(assistant);
            context.Notify();
        }

        if (string.IsNullOrWhiteSpace(assistant.Content) && assistant.Metadata is null)
            document.Messages.Remove(assistant);

        if (completionTokens is int generated && stopwatch.Elapsed.TotalSeconds > 0.1)
            lastTps = generated / stopwatch.Elapsed.TotalSeconds;
        else if (assistant.Content.Length > 0 && stopwatch.Elapsed.TotalSeconds > 0.1)
            lastTps = (assistant.Content.Length / 4.0) / stopwatch.Elapsed.TotalSeconds;

        document.LastTokensPerSecond = lastTps;
        document.LastCost = cost;
        document.LastPromptTokens = promptTokens;
        document.LastCompletionTokens = completionTokens;
        document.LastToolCalls = toolCallsUsed;
        context.SetStatus(cost is { } spent
            ? $"Ready. OpenRouter cost {spent:0.000000}."
            : promptTokens is int promptUsed && completionTokens is int completionUsed
                ? $"Ready. {promptUsed:N0} prompt / {completionUsed:N0} completion tokens."
                : "Ready.");
    }

    private string? ResolveModel(ChatSessionDocument document) =>
        string.IsNullOrWhiteSpace(document.RemoteModel)
            ? settings.DefaultModel
            : document.RemoteModel;

    private static void AccumulateToolCall(
        Dictionary<int, ToolCallAccumulator> accumulators,
        OpenRouterToolCallDelta delta)
    {
        if (!accumulators.TryGetValue(delta.Index, out ToolCallAccumulator? call))
        {
            call = new ToolCallAccumulator { Index = delta.Index };
            accumulators[delta.Index] = call;
        }

        if (!string.IsNullOrWhiteSpace(delta.Id)) call.Id = delta.Id;
        if (!string.IsNullOrWhiteSpace(delta.Name)) call.Name = delta.Name;
        if (!string.IsNullOrEmpty(delta.Arguments)) call.Arguments.Append(delta.Arguments);
    }

    /// <summary>
    /// ChatMessage.Metadata is init-only, so attach the tool-call payload by
    /// replacing the row in place. The transcript filter hides the empty
    /// assistant row; the message builder reads the metadata on reload.
    /// </summary>
    private static void AttachToolCalls(
        List<ChatMessage> messages,
        ChatMessage assistant,
        JsonArray toolCalls)
    {
        int index = messages.IndexOf(assistant);
        if (index < 0) return;

        var metadata = assistant.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(assistant.Metadata);
        metadata[OpenRouterMetadata.ToolCalls] = toolCalls.ToJsonString();

        messages[index] = new ChatMessage
        {
            Role = assistant.Role,
            Content = assistant.Content,
            Name = assistant.Name,
            Artifacts = assistant.Artifacts,
            Timestamp = assistant.Timestamp,
            Metadata = metadata,
            IsPinned = assistant.IsPinned,
            Ignore = assistant.Ignore,
        };
    }

    private sealed class ToolCallAccumulator
    {
        public required int Index { get; init; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();

        public OpenRouterCompletedToolCall ToCall() => new(
            Index,
            string.IsNullOrWhiteSpace(Id) ? $"call_{Guid.NewGuid():n}" : Id,
            Name,
            Arguments.ToString());
    }
}
