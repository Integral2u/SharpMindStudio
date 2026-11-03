using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using System.Text.Json;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CorpusMergerTests
{
    [Fact]
    public async Task Merge_WritesJsonlAndRemovesDuplicates()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("books");
        Directory.CreateDirectory(source);

        // The Gutenberg marker is deliberately kept: the merge tool no longer
        // knows about any particular corpus. A regex or a cleaning stage is the
        // right place for that rule.
        await File.WriteAllTextAsync(temp.Combine("books", "one.txt"),
            "*** START OF THE PROJECT GUTENBERG EBOOK ONE ***\n" +
            "He said \u201Chello\u201D and left.\n\n" +
            "*** END OF THE PROJECT GUTENBERG EBOOK ONE ***\n");
        await File.WriteAllTextAsync(temp.Combine("books", "two.html"),
            "<html><style>.x{color:red}</style><body>" +
            "<p>He said &quot;hello&quot; and left.</p><script>bad()</script></body></html>");
        await File.WriteAllTextAsync(temp.Combine("books", "three.html"),
            "<html><style>.x{color:red}</style><body>" +
            "<p>He said &quot;hello&quot; and left.</p><script>bad()</script></body></html>");
        await File.WriteAllTextAsync(temp.Combine("books", "ignored.md"), "not part of the corpus");

        string output = temp.Combine("out", "merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt", ".html"],
        });

        Assert.Equal(3, result.FilesRead);
        Assert.Equal(2, result.Documents);
        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.Equal(CorpusMergeFormat.Jsonl, result.Format);
        Assert.True(File.Exists(output));

        string[] lines = await File.ReadAllLinesAsync(output);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("PROJECT GUTENBERG", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("hello", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("PROJECT GUTENBERG", StringComparison.Ordinal));
        Assert.DoesNotContain("<p>", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.DoesNotContain("bad()", string.Join('\n', lines), StringComparison.Ordinal);

        // The output is directly consumable by the library's JSONL source.
        var jsonl = new JsonlSource(output);
        var documents = new List<string>();
        await foreach (string document in jsonl.ReadAsync())
            documents.Add(document);
        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, document => document.Contains("PROJECT GUTENBERG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Merge_TextFormatUsesTheCallerSeparator()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("books");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("books", "a.txt"), "First document.");
        await File.WriteAllTextAsync(temp.Combine("books", "b.txt"), "Second document.");

        string output = temp.Combine("merged.txt");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Format = CorpusMergeFormat.Text,
            TextSeparator = "\n---\n",
        });

        Assert.Equal(2, result.Documents);
        string text = await File.ReadAllTextAsync(output);
        Assert.Contains("First document.\n---\nSecond document.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<|endoftext|>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_DropPatternIsSuppliedByTheCaller()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("books");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("books", "a.txt"), "Keep this document.");
        await File.WriteAllTextAsync(temp.Combine("books", "b.txt"), "Drop this boilerplate document.");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            DropDocumentPattern = "boilerplate",
        });

        Assert.Equal(2, result.FilesRead);
        Assert.Equal(1, result.Documents);
        Assert.Equal(1, result.Dropped);
        string[] lines = await File.ReadAllLinesAsync(output);
        Assert.Single(lines);
        Assert.Contains("Keep this document", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_QaPairsKeepEachAnswerWithItsQuestion()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("qa");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("qa", "faq.txt"),
            "Q: What is A?\nA: First answer.\n\n" +
            "Q: What is B?\nA: Second answer.\n");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt"],
            DocumentLayout = CorpusDocumentLayout.QaPairs,
        });

        Assert.Equal(2, result.Documents);
        string[] lines = await File.ReadAllLinesAsync(output);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        string firstText = first.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Q: What is A?\nA: First answer.", firstText, StringComparison.Ordinal);

        using var second = JsonDocument.Parse(lines[1]);
        string secondText = second.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Q: What is B?\nA: Second answer.", secondText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_NumberedQaMarkersAreDetected()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("qa");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("qa", "numbered.txt"),
            "Q1: First?\nA1: One.\nQ2: Second?\nA2: Two.");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt"],
        });

        Assert.Equal(2, result.Documents);
    }

    [Fact]
    public async Task Merge_BlankLineSplitsClearEntries()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("entries");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("entries", "entries.txt"),
            "First entry.\n\nSecond entry.\n\n\nThird entry.");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt"],
            DocumentLayout = CorpusDocumentLayout.BlankLine,
        });

        Assert.Equal(3, result.Documents);
        string[] lines = await File.ReadAllLinesAsync(output);
        Assert.Equal(3, lines.Length);
        Assert.Contains("First entry", lines[0], StringComparison.Ordinal);
        Assert.Contains("Second entry", lines[1], StringComparison.Ordinal);
        Assert.Contains("Third entry", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_AutoDetectsQaPairs()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("qa");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("qa", "faq.txt"),
            "Question: First?\nAnswer: One.\nQuestion: Second?\nAnswer: Two.");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt"],
        });

        Assert.Equal(CorpusDocumentLayout.Auto, result.Layout);
        Assert.Equal(2, result.Documents);
    }

    [Fact]
    public async Task Merge_HtmlBlocksSplitsParagraphs()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("html");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("html", "page.html"),
            "<html><body><p>First paragraph.</p><p>Second paragraph.</p></body></html>");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".html"],
            DocumentLayout = CorpusDocumentLayout.HtmlBlocks,
        });

        Assert.Equal(2, result.Documents);
        string[] lines = await File.ReadAllLinesAsync(output);
        Assert.Contains("First paragraph", lines[0], StringComparison.Ordinal);
        Assert.Contains("Second paragraph", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_WholeFileKeepsAQaFileTogether()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("qa");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("qa", "faq.txt"),
            "Q: First?\nA: One.\nQ: Second?\nA: Two.");

        string output = temp.Combine("merged.jsonl");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output, new CorpusMergeOptions
        {
            Extensions = [".txt"],
            DocumentLayout = CorpusDocumentLayout.WholeFile,
        });

        Assert.Equal(1, result.Documents);
    }

    [Fact]
    public async Task Merge_RequiresAnExistingSourceFolder()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => CorpusMerger.MergeAsync(temp.Combine("missing"), temp.Combine("out.jsonl")));
    }
}
