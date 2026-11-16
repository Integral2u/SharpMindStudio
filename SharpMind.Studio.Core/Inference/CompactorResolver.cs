using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Core.Inference;

/// <summary>
/// Maps a profile's Compactor value to a built-in or plugin compactor. Kept in
/// Core so the chat and agent sessions resolve the same way.
/// </summary>
public static class CompactorResolver
{
    public const string None = "None";
    public const string Summarize = "Summarize";
    public const string Truncate = "Truncate";

    public static IContextCompactor? Resolve(
        string? name,
        IEnumerable<IContextCompactor>? plugins = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string value = name.Trim();
        if (value.Equals(None, StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Equals(Summarize, StringComparison.OrdinalIgnoreCase))
            return new SummarizingCompactor();
        if (value.Equals(Truncate, StringComparison.OrdinalIgnoreCase))
            return new TruncatingCompactor();

        return plugins?.FirstOrDefault(plugin =>
            plugin.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
