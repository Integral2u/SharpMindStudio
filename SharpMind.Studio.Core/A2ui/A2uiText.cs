using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.A2ui;

public sealed record A2uiExtraction(string Content, IReadOnlyList<string> Blocks);

/// <summary>Finds fenced a2ui blocks in an assistant response and hides them
/// while the reply is still streaming.</summary>
public static partial class A2uiText
{
    [GeneratedRegex(
        @"(?is)(?:```|~~~)[ \t]*a2ui[ \t]*\r?\n?(?<json>.*?)(?:```|~~~)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex(@"(?is)(?:```|~~~)[ \t]*a2ui", RegexOptions.CultureInvariant)]
    private static partial Regex OpenFence();

    public static A2uiExtraction Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new A2uiExtraction(content ?? "", []);

        MatchCollection matches = Fence().Matches(content);
        if (matches.Count == 0)
            return new A2uiExtraction(content, []);

        var blocks = new List<string>();
        foreach (Match match in matches)
        {
            string json = match.Groups["json"].Value.Trim();
            if (json.Length > 0) blocks.Add(json);
        }

        string cleaned = Fence().Replace(content, "").Trim();
        return new A2uiExtraction(cleaned, blocks);
    }

    public static bool HasUnclosedBlock(string? content) =>
        OpenFenceIndex(content) >= 0;

    /// <summary>Drops an unterminated fence and everything after it.</summary>
    public static string RemoveOpenBlock(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        int index = OpenFenceIndex(content);
        return index < 0 ? content : content[..index].TrimEnd();
    }

    private static int OpenFenceIndex(string? content)
    {
        if (string.IsNullOrEmpty(content)) return -1;
        A2uiExtraction extracted = Extract(content);
        Match open = OpenFence().Match(extracted.Content);
        if (!open.Success) return -1;

        int close = extracted.Content.IndexOf(
            "```", open.Index + open.Length, StringComparison.Ordinal);
        int alternate = extracted.Content.IndexOf(
            "~~~", open.Index + open.Length, StringComparison.Ordinal);
        if (close < 0 || (alternate >= 0 && alternate < close)) close = alternate;
        return close < 0 ? open.Index : -1;
    }

    /// <summary>Removes complete blocks and truncates an unterminated block so
    /// the streaming bubble does not show raw JSON.</summary>
    public static string HideStreaming(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";

        int open = OpenFenceIndex(content);
        if (open < 0) return Extract(content).Content;

        string before = content[..open].TrimEnd();
        return before.Length == 0
            ? "Building interface..."
            : before + "\n\nBuilding interface...";
    }
}
