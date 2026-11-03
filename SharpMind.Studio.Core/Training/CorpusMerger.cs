using System.Text;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Training;

public sealed record CorpusMergeResult(
    int FilesRead,
    int Documents,
    int DuplicatesRemoved,
    long OutputBytes,
    string OutputPath);

/// <summary>
/// Merges a folder of text/HTML files into one clean corpus. Ported from the
/// SharpMind sandbox helper: Gutenberg headers are dropped, HTML is stripped,
/// paragraphs are normalised, and exact duplicates are removed before the
/// documents are joined with an end-of-text marker.
/// </summary>
public static class CorpusMerger
{
    private static readonly string[] Extensions = [".txt", ".html", ".htm"];

    /// <summary>A rare marker reads as a real boundary to a tokenizer.</summary>
    public const string DocumentSeparator = "\n<|endoftext|>\n";

    public static async Task<CorpusMergeResult> MergeAsync(
        string sourceFolder,
        string outputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        string root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Source folder not found: {root}");

        List<string> files = [.. Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)];

        var contents = new List<string>();
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            string cleaned = NormalizeParagraphs(StripMarkup(text));
            if (cleaned.Length != 0)
                contents.Add(cleaned);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        int duplicates = 0;
        foreach (string item in contents)
        {
            if (seen.Add(item)) unique.Add(item);
            else duplicates++;
        }

        string destination = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string merged = string.Join(DocumentSeparator, unique);
        await File.WriteAllTextAsync(destination, merged, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        return new CorpusMergeResult(
            files.Count,
            unique.Count,
            duplicates,
            Encoding.UTF8.GetByteCount(merged),
            destination);
    }

    private static string StripMarkup(string text)
    {
        // Project Gutenberg wraps every book in a license header/footer. Drop
        // everything outside the START/END markers so the model is not trained
        // on the boilerplate that would otherwise repeat once per book.
        Match start = Regex.Match(text, @"\*\*\*\s*START OF (?:THE|THIS) PROJECT GUTENBERG EBOOK[^\r\n]*\*\*\*", RegexOptions.IgnoreCase);
        if (start.Success)
            text = text[(start.Index + start.Length)..];
        Match end = Regex.Match(text, @"\*\*\*\s*END OF (?:THE|THIS) PROJECT GUTENBERG EBOOK[^\r\n]*\*\*\*", RegexOptions.IgnoreCase);
        if (end.Success)
            text = text[..end.Index];

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
    /// paragraphs with one newline.
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
        // Collapse internal whitespace runs (Gutenberg pads with spaces).
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
