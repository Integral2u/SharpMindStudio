using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SharpMind.Data.Sources;

namespace SharpMind.Studio.Core.DataFactory;

/// <summary>One input document after cleaning and filtering.</summary>
public sealed record DataFactoryDocument(int Index, string Text, string Source);

/// <summary>
/// Reads a recipe's source and applies its cleaning/filter rules. Text sources
/// are whole files; JSONL sources read the configured text field. Documents are
/// returned in stable path order so a recipe run is reproducible.
/// </summary>
public static class DataFactoryInput
{
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".markdown", ".html", ".htm", ".xml", ".csv"];

    public static async IAsyncEnumerable<DataFactoryDocument> ReadAsync(
        DataFactoryRecipe recipe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipe.SourcePath))
            throw new InvalidOperationException("Choose a source path first.");

        Regex? drop = null;
        if (!string.IsNullOrWhiteSpace(recipe.DropPattern))
        {
            try
            {
                drop = new Regex(
                    recipe.DropPattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid drop regex: {ex.Message}", ex);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;

        if (recipe.SourceFormat == DataFactorySourceFormat.Jsonl)
        {
            var source = new JsonlSource(recipe.SourcePath, recipe.TextField);
            await foreach (string text in source.ReadAsync(cancellationToken))
            {
                string? cleaned = Clean(text, recipe, drop, sourceLabel: recipe.SourcePath);
                if (cleaned is null) continue;
                if (recipe.Deduplicate && !seen.Add(cleaned)) continue;
                yield return new DataFactoryDocument(index++, cleaned, recipe.SourcePath);
            }
            yield break;
        }

        foreach (string file in ResolveTextFiles(recipe.SourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string raw = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            string? cleaned = Clean(raw, recipe, drop, sourceLabel: file);
            if (cleaned is null) continue;
            if (recipe.Deduplicate && !seen.Add(cleaned)) continue;
            yield return new DataFactoryDocument(index++, cleaned, file);
        }
    }

    public static IEnumerable<string> ResolveTextFiles(string path)
    {
        string value = path.Trim();
        if (Directory.Exists(value))
        {
            return Directory.EnumerateFiles(value, "*.*", SearchOption.AllDirectories)
                .Where(IsTextFile)
                .OrderBy(file => file, StringComparer.Ordinal);
        }

        if (value.Contains('*') || value.Contains('?'))
            return GlobResolver.Resolve(value).Where(IsTextFile).OrderBy(file => file, StringComparer.Ordinal);

        if (File.Exists(value)) return [Path.GetFullPath(value)];
        throw new FileNotFoundException($"Source path not found: {value}", value);
    }

    private static bool IsTextFile(string path) =>
        TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? Clean(
        string raw,
        DataFactoryRecipe recipe,
        Regex? drop,
        string sourceLabel)
    {
        string text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        if (recipe.StripHtml && IsHtml(sourceLabel))
            text = StripHtml(text);

        if (recipe.NormalizeText)
        {
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @" *\n *", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
        }

        text = text.Trim();
        if (text.Length == 0) return null;
        if (recipe.MinChars > 0 && text.Length < recipe.MinChars) return null;
        if (recipe.MaxChars > 0 && text.Length > recipe.MaxChars) return null;
        if (drop is not null && drop.IsMatch(text)) return null;
        return text;
    }

    private static bool IsHtml(string source) =>
        source.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static string StripHtml(string html)
    {
        string text = Regex.Replace(
            html,
            @"<(script|style)\b[^>]*>.*?</\1>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
        text = Regex.Replace(text, @"<[^>]+>", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        text = WebUtility.HtmlDecode(text);
        return text;
    }
}

/// <summary>Renders the user prompt template for one document.</summary>
public static class DataFactoryPrompt
{
    public static string Render(string template, DataFactoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string value = string.IsNullOrWhiteSpace(template)
            ? "{text}"
            : template;

        value = value
            .Replace("{text}", document.Text, StringComparison.Ordinal)
            .Replace("{source}", document.Source, StringComparison.Ordinal)
            .Replace("{id}", document.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{name}", Path.GetFileName(document.Source), StringComparison.Ordinal);

        if (!template.Contains("{text}", StringComparison.Ordinal))
            value += "\n\n" + document.Text;

        return value;
    }
}
