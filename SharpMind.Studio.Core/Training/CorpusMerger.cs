using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Training;

public enum CorpusMergeFormat
{
    Jsonl,
    Text,
}

/// <summary>
/// What a merge run should do. The defaults are deliberately generic: text and
/// HTML files become one JSONL document per line, which the library's
/// <c>JsonlSource</c> reads without a tokenizer-specific boundary. Boilerplate
/// belongs to a user-supplied regex or a cleaning stage, not to this tool.
/// </summary>
public sealed record CorpusMergeOptions
{
    public CorpusMergeFormat Format { get; init; } = CorpusMergeFormat.Jsonl;

    /// <summary>File extensions to read. Comparison is case-insensitive.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [".txt", ".html", ".htm"];

    public bool StripHtml { get; init; } = true;
    public bool NormalizeText { get; init; } = true;
    public bool Deduplicate { get; init; } = true;

    /// <summary>Drops a cleaned document when it matches this regex, if supplied.</summary>
    public string? DropDocumentPattern { get; init; }

    /// <summary>Only used by <see cref="CorpusMergeFormat.Text"/>.</summary>
    public string TextSeparator { get; init; } = "\n\n";
}

public sealed record CorpusMergeResult(
    int FilesRead,
    int Documents,
    int DuplicatesRemoved,
    int Dropped,
    long OutputBytes,
    string OutputPath,
    CorpusMergeFormat Format);

/// <summary>
/// Merges a folder of text/HTML files into one corpus. This is a convenience
/// tool, not a dataset pipeline: document boundaries are preserved as JSONL
/// records (or a caller-supplied text separator), and corpus-specific cleanup
/// is a stage or a regex supplied by the user.
/// </summary>
public static class CorpusMerger
{
    public static async Task<CorpusMergeResult> MergeAsync(
        string sourceFolder,
        string outputFile,
        CorpusMergeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        options ??= new CorpusMergeOptions();
        string root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Source folder not found: {root}");

        Regex? dropPattern = null;
        if (!string.IsNullOrWhiteSpace(options.DropDocumentPattern))
        {
            try
            {
                dropPattern = new Regex(
                    options.DropDocumentPattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid drop regex: {ex.Message}", ex);
            }
        }

        HashSet<string> extensions = new(
            options.Extensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase);
        List<string> files = [.. Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => extensions.Count == 0 || extensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.Ordinal)];

        string destination = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        int filesRead = 0;
        int documents = 0;
        int duplicates = 0;
        int dropped = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using (var stream = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65_536, useAsync: true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.NewLine = "\n";

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesRead++;

                string text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                string cleaned = Clean(text, Path.GetExtension(file), options);
                if (cleaned.Length == 0) continue;

                if (dropPattern is not null && dropPattern.IsMatch(cleaned))
                {
                    dropped++;
                    continue;
                }

                if (options.Deduplicate && !seen.Add(cleaned))
                {
                    duplicates++;
                    continue;
                }

                if (documents > 0 && options.Format == CorpusMergeFormat.Text)
                    await writer.WriteAsync(options.TextSeparator).ConfigureAwait(false);

                if (options.Format == CorpusMergeFormat.Jsonl)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { text = cleaned }))
                        .ConfigureAwait(false);
                else
                    await writer.WriteAsync(cleaned).ConfigureAwait(false);

                documents++;
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        long bytes = new FileInfo(destination).Length;
        return new CorpusMergeResult(
            filesRead, documents, duplicates, dropped, bytes, destination, options.Format);
    }

    private static string Clean(string text, string extension, CorpusMergeOptions options)
    {
        string cleaned = text;
        if (options.StripHtml
            && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)))
        {
            cleaned = StripHtml(cleaned);
        }

        return options.NormalizeText ? NormalizeParagraphs(cleaned) : cleaned.Trim();
    }

    private static string NormalizeExtension(string extension)
    {
        string value = extension.Trim();
        return value.StartsWith('.') ? value : "." + value;
    }

    private static string StripHtml(string text)
    {
        string stripped = Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        stripped = Regex.Replace(stripped, @"<script[^>]*>.*?</script>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"<style[^>]*>.*?</style>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Block-level tags become paragraph breaks (not spaces) so HTML keeps
        // its structure; everything else collapses to a space.
        stripped = Regex.Replace(stripped, @"</?(?:p|div|br|hr|li|ul|ol|tr|td|th|table|h[1-6]|blockquote|section|article|pre|header|footer)\b[^>]*>", "\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"<[^>]+>", " ", RegexOptions.Singleline);
        stripped = Regex.Replace(stripped, @"&nbsp;", " ", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&amp;", "&", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&lt;", "<", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&gt;", ">", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&quot;", "\"", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&#39;|&apos;", "'", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"&#(\d+);", match => char.ConvertFromUtf32(int.Parse(match.Groups[1].Value)));
        stripped = Regex.Replace(stripped, @"[ \t]+", " ");
        return stripped;
    }

    /// <summary>
    /// Turns a document into paragraph-structured text: normalizes Unicode
    /// punctuation, unwraps hard-wrapped lines inside a paragraph, and joins
    /// paragraphs with one newline. This is generic prose cleanup, not a
    /// corpus-specific rule.
    /// </summary>
    private static string NormalizeParagraphs(string text)
    {
        text = NormalizePunctuation(text);

        string[] lines = Regex.Split(text, @"\r\n|\r|\n");
        var paragraphs = new List<string>();
        var current = new StringBuilder();

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Flush(paragraphs, current);
                continue;
            }

            line = NormalizeLine(line);
            if (line.Length == 0)
                continue;

            if (current.Length > 0)
                current.Append(' ');
            current.Append(line);
        }
        Flush(paragraphs, current);

        return string.Join("\n", paragraphs).Trim();
    }

    private static void Flush(List<string> paragraphs, StringBuilder current)
    {
        if (current.Length == 0)
            return;
        paragraphs.Add(current.ToString());
        current.Clear();
    }

    private static string NormalizeLine(string line)
    {
        // Collapse internal whitespace runs.
        line = Regex.Replace(line, @"\s+", " ");
        // Keep only printable text: strip control characters but preserve every
        // visible glyph, including letters above ASCII.
        var builder = new StringBuilder(line.Length);
        foreach (char character in line)
        {
            if (char.IsControl(character))
                continue;
            builder.Append(character);
        }
        // Chapter/section headings are meaningful structure, so keep them; only
        // drop decorative rules or fragments that carry no letters or digits.
        if (!builder.ToString().Any(char.IsLetterOrDigit))
            return string.Empty;
        return builder.ToString();
    }

    private static string NormalizePunctuation(string text)
    {
        return text
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u201F', '"')
            .Replace('\u2014', '-').Replace('\u2013', '-').Replace('\u2012', '-').Replace('\u2015', '-')
            .Replace('\u2026', '.')     // rendered as a single period; keeps spacing simple
            .Replace('\u00A0', ' ')     // non-breaking space
            .Replace('\u2022', '*').Replace('\u00B7', '.')
            .Replace('\u00AB', '"').Replace('\u00BB', '"')
            .Replace('\u2032', '\'').Replace('\u2033', '"');
    }
}
