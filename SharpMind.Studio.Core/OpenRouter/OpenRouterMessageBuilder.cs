using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>Metadata keys used to persist an OpenAI-style tool exchange in a chat document.</summary>
public static class OpenRouterMetadata
{
    public const string ToolCalls = "openrouter.tool_calls";
    public const string ToolCallId = "openrouter.tool_call_id";
}

/// <summary>A completed tool call assembled from streamed argument fragments.</summary>
public sealed record OpenRouterCompletedToolCall(int Index, string Id, string Name, string Arguments);

/// <summary>
/// Pure conversions between SharpMind chat state and the OpenAI-compatible
/// message/tool shapes OpenRouter expects. Kept separate so the mapping can be
/// tested without a network or a UI.
/// </summary>
public static class OpenRouterMessageBuilder
{
    public static List<JsonObject> BuildMessages(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        IAgentBuilder? agent)
    {
        var messages = new List<JsonObject>();

        if (agent is not null)
        {
            foreach (string prompt in agent.AdditionalSystemPrompts)
                AddSystem(messages, prompt);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                AddSystem(messages, systemPrompt);
            AddSystem(messages, agent.BuildAgentPrompt());
        }
        else if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            AddSystem(messages, systemPrompt);
        }

        foreach (ChatMessage message in history)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    // System prompts are supplied once above; history copies are skipped.
                    break;

                case ChatRole.User:
                    messages.Add(Text("user", message.Content));
                    break;

                case ChatRole.Agent:
                    if (message.Metadata?.TryGetValue(OpenRouterMetadata.ToolCalls, out string? toolsJson) == true
                        && !string.IsNullOrWhiteSpace(toolsJson)
                        && JsonNode.Parse(toolsJson) is JsonArray toolCalls)
                    {
                        var assistant = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = string.IsNullOrWhiteSpace(message.Content) ? null : message.Content,
                            ["tool_calls"] = toolCalls,
                        };
                        messages.Add(assistant);
                    }
                    else if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        messages.Add(Text("assistant", message.Content));
                    }
                    break;

                case ChatRole.Tool:
                    string callId = message.Metadata?.GetValueOrDefault(OpenRouterMetadata.ToolCallId) ?? "";
                    messages.Add(ToolResult(callId, message.Content));
                    break;
            }
        }

        return messages;
    }

    public static IReadOnlyList<JsonObject> BuildTools(IAgentBuilder agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var tools = new List<JsonObject>();
        foreach (JsonObject definition in agent.ToolDefinitions.OfType<JsonObject>())
        {
            string? name = definition["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            JsonObject parameters = definition["parameters"]?.DeepClone() as JsonObject
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = definition["description"]?.GetValue<string>() ?? "",
                    ["parameters"] = parameters,
                },
            });
        }
        return tools;
    }

    public static JsonArray ToToolCallsJson(IEnumerable<OpenRouterCompletedToolCall> calls)
    {
        var array = new JsonArray();
        foreach (OpenRouterCompletedToolCall call in calls.OrderBy(call => call.Index))
        {
            array.Add(new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments,
                },
            });
        }
        return array;
    }

    public static JsonObject User(string content) => Text("user", content);
    public static JsonObject Assistant(string content) => Text("assistant", content);
    public static JsonObject System(string content) => Text("system", content);

    public static JsonObject ToolResult(string toolCallId, string content) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = toolCallId,
        ["content"] = content,
    };

    /// <summary>
    /// Formats a tool result the way the local chat session does: a header naming
    /// the tool and key arguments, then the data as plain text. OpenAI-style tool
    /// messages otherwise carry raw JSON, which some models misread as a new
    /// instruction (for example re-listing UI choices instead of using the picked
    /// value).
    /// </summary>
    public static string FormatToolResult(
        string toolName,
        JsonObject arguments,
        JsonObject result,
        int maxChars = 16_000)
    {
        string status = result["status"]?.GetValue<string>() ?? "success";
        string body = result["data"]?.GetValue<string>()
            ?? result["message"]?.GetValue<string>()
            ?? result.ToJsonString();
        if (result["notes"] is JsonNode notes)
            body += "\n(" + notes.GetValue<string>() + ")";

        string header = $"[{toolName}{KeyArguments(arguments)}]";
        string text = status == "success"
            ? $"{header} {body}"
            : $"{header} ERROR: {body}";

        return text.Length <= maxChars
            ? text
            : text[..maxChars] + "\n... [tool result truncated]";
    }

    private static string KeyArguments(JsonObject arguments)
    {
        if (arguments.Count == 0) return "";
        string preview = arguments.ToJsonString();
        if (preview.Length > 160) preview = preview[..160] + "...";
        return $" {preview}";
    }

    private static void AddSystem(List<JsonObject> messages, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            messages.Add(Text("system", content));
    }

    private static JsonObject Text(string role, string content) => new()
    {
        ["role"] = role,
        ["content"] = content,
    };
}
