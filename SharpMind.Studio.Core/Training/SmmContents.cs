using SharpMind.Model;
using SharpMind.Model.Format;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// What an <c>.smm</c> container carries besides weights. Read-only: nothing
/// here loads or executes an embedded assembly.
/// </summary>
public sealed record SmmContents(
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Skills,
    bool HasSystemPrompt,
    IReadOnlyDictionary<string, string> Lineage)
{
    public static SmmContents None { get; } = new(
        [], [], false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => Plugins.Count == 0 && Skills.Count == 0 && !HasSystemPrompt;

    /// <summary>One line for the UI, or an empty string when the file carries nothing extra.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Plugins.Count > 0)
                parts.Add($"{Plugins.Count} plugin {(Plugins.Count == 1 ? "assembly" : "assemblies")}");
            if (Skills.Count > 0)
                parts.Add($"{Skills.Count} skill{(Skills.Count == 1 ? "" : "s")}");
            if (HasSystemPrompt) parts.Add("an embedded system prompt");
            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Reads the non-weight sections of an <c>.smm</c> for display. Studio never
/// activates an embedded plugin; this exists so the file's contents are visible
/// before training inherits them.
/// </summary>
public static class SmmContentsReader
{
    /// <summary>
    /// The embedded plugin assemblies with their bytes, for a session that has
    /// been explicitly allowed to load them. Empty for anything but an .smm.
    /// </summary>
    public static IReadOnlyList<SmmPluginEntry> ReadPlugins(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        if (!path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)) return [];
        if (!File.Exists(path)) return [];

        try
        {
            return SmmLoader.LoadPlugins(path);
        }
        catch
        {
            return [];
        }
    }

    public static SmmContents Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return SmmContents.None;
        if (!path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)) return SmmContents.None;
        if (!File.Exists(path)) return SmmContents.None;

        try
        {
            ModelMetaData meta = SmmLoader.LoadMeta(path);
            List<string> skills = SmmLoader.LoadSkillsFromMeta(meta);
            string? prompt = SmmLoader.LoadSystemPromptFromMeta(meta);
            Dictionary<string, string> lineage = SmmLoader.LoadLineageFromMeta(meta);

            List<string> plugins = [];
            try
            {
                plugins = [.. SmmLoader.LoadPlugins(path).Select(entry => entry.Name)];
            }
            catch
            {
                // A malformed plugin section must not hide the rest of the report.
            }

            return new SmmContents(
                plugins,
                [.. skills.Select(SkillName)],
                !string.IsNullOrWhiteSpace(prompt),
                lineage);
        }
        catch
        {
            return SmmContents.None;
        }
    }

    private static string SkillName(string markdown)
    {
        foreach (string line in markdown.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length > 0) return trimmed.Length > 60 ? trimmed[..60] : trimmed;
        }
        return "Untitled skill";
    }
}
