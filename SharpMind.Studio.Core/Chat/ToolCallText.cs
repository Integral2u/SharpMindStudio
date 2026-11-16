namespace SharpMind.Studio.Core.Chat;

/// <summary>
/// Detects tool-call-shaped text that a model printed instead of invoking. The
/// chat UI uses it to explain a raw tool card/schema rather than leaving the
/// person with an unexplained block.
/// </summary>
public static class ToolCallText
{
    public static bool LooksUnparsed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)
            || text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string lower = text.ToLowerInvariant();
        bool call = (lower.Contains("\"tool\"", StringComparison.Ordinal)
                || lower.Contains("\"function\"", StringComparison.Ordinal))
            && (lower.Contains("\"arguments\"", StringComparison.Ordinal)
                || lower.Contains("\"parameters\"", StringComparison.Ordinal));
        bool schema = lower.Contains("\"name\"", StringComparison.Ordinal)
            && lower.Contains("\"description\"", StringComparison.Ordinal)
            && lower.Contains("\"parameters\"", StringComparison.Ordinal);
        return call || schema;
    }
}
