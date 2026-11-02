using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMind.Inference;
using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Core.Chat;

/// <summary>
/// Removes tool machinery from what Studio shows to a person. Chat history keeps
/// the model's original call text and the tool result messages so the next turn
/// has full context; this helper only cleans the transcript view.
/// </summary>
public static class ToolCallTextSanitizer
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        string result = text;
        result = RegexGenerated.ToolCallBlocks.Replace(result, string.Empty);
        result = RegexGenerated.FunctionBlocks.Replace(result, string.Empty);
        result = CutFrom(result, "<tool_call");
        result = CutFrom(result, "<function_call");
        result = result
            .Replace("[TOOL_CALLS]", string.Empty, StringComparison.Ordinal)
            .Replace("<|python_tag|>", string.Empty, StringComparison.Ordinal)
            .Replace("<python_tag>", string.Empty, StringComparison.Ordinal);
        result = RemoveJsonCalls(result);

        return string.Equals(result, text, StringComparison.Ordinal) ? text : result.Trim();
    }

    private static string CutFrom(string text, string marker)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    private static string RemoveJsonCalls(string text)
    {
        if (!text.Contains('{')) return text;

        var output = new StringBuilder(text.Length);
        int cursor = 0;
        while (cursor < text.Length)
        {
            int open = text.IndexOf('{', cursor);
            if (open < 0)
            {
                output.Append(text, cursor, text.Length - cursor);
                break;
            }

            output.Append(text, cursor, open - cursor);
            int end = FindObjectEnd(text, open);
            if (end < 0)
            {
                // An unclosed call at the end of the stream (or a truncated
                // generation). Drop it rather than show a JSON fragment.
                if (LooksLikeToolCallStart(text[open..])) break;
                output.Append(text[open]);
                cursor = open + 1;
                continue;
            }

            string candidate = text[open..(end + 1)];
            if (IsToolCallJson(candidate))
            {
                cursor = end + 1;
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                continue;
            }

            output.Append(candidate);
            cursor = end + 1;
        }

        return output.ToString();
    }

    private static int FindObjectEnd(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private static bool IsToolCallJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return false;
            if (obj["tool_calls"] is JsonArray) return true;

            string? name = obj["name"]?.GetValue<string>() ?? obj["tool"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) return false;
            return obj.ContainsKey("arguments") || obj.ContainsKey("args");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LooksLikeToolCallStart(string tail)
    {
        if (!tail.StartsWith('{')) return false;
        ReadOnlySpan<char> head = tail.AsSpan(0, Math.Min(tail.Length, 80));
        return head.Contains("\"name\"", StringComparison.Ordinal)
            || head.Contains("\"tool\"", StringComparison.Ordinal)
            || head.Contains("\"tool_calls\"", StringComparison.Ordinal);
    }
}

/// <summary>Builds the chat transcript: no system/tool rows, no tool-call text.</summary>
public static class ChatTranscriptFilter
{
    /// <param name="keepEmptyLastAgent">
    /// Keep the trailing assistant row while it is empty, so the streaming bubble
    /// has somewhere to appear before the first token arrives.
    /// </param>
    public static IReadOnlyList<ChatMessage> Visible(
        IEnumerable<ChatMessage> messages,
        bool keepEmptyLastAgent = false)
    {
        List<ChatMessage> source = [.. messages];
        var visible = new List<ChatMessage>(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            ChatMessage message = source[i];
            if (message.Role is ChatRole.System or ChatRole.Tool)
                continue;

            if (message.Role != ChatRole.Agent)
            {
                visible.Add(message);
                continue;
            }

            string content = ToolCallTextSanitizer.Sanitize(message.Content);
            bool keepEmpty = keepEmptyLastAgent && i == source.Count - 1;
            if (string.IsNullOrWhiteSpace(content) && !keepEmpty)
                continue;

            if (string.Equals(content, message.Content, StringComparison.Ordinal))
            {
                visible.Add(message);
                continue;
            }

            visible.Add(new ChatMessage
            {
                Role = message.Role,
                Content = content,
                Name = message.Name,
                Artifacts = message.Artifacts,
                Timestamp = message.Timestamp,
                Metadata = message.Metadata,
                IsPinned = message.IsPinned,
                Ignore = message.Ignore,
            });
        }

        return visible;
    }
}
