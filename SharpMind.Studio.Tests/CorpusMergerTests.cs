using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CorpusMergerTests
{
    [Fact]
    public async Task Merge_CleansGutenbergHtmlAndRemovesDuplicates()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("books");
        Directory.CreateDirectory(source);

        await File.WriteAllTextAsync(temp.Combine("books", "one.txt"),
            "*** START OF THE PROJECT GUTENBERG EBOOK ONE ***\n" +
            "He said \u201Chello\u201D and left.\n\n" +
            "*** END OF THE PROJECT GUTENBERG EBOOK ONE ***\n");
        await File.WriteAllTextAsync(temp.Combine("books", "two.html"),
            "<html><style>.x{color:red}</style><body>" +
            "<p>He said &quot;hello&quot; and left.</p><script>bad()</script></body></html>");
        await File.WriteAllTextAsync(temp.Combine("books", "ignored.md"), "not part of the corpus");

        string output = temp.Combine("out", "merged.txt");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output);

        Assert.Equal(2, result.FilesRead);
        Assert.Equal(1, result.Documents);
        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.True(File.Exists(output));

        string text = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("PROJECT GUTENBERG", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bad()", text, StringComparison.Ordinal);
        Assert.Contains("hello", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Merge_WritesTheDocumentSeparator()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("books");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(temp.Combine("books", "a.txt"), "First document.");
        await File.WriteAllTextAsync(temp.Combine("books", "b.txt"), "Second document.");

        string output = temp.Combine("merged.txt");
        CorpusMergeResult result = await CorpusMerger.MergeAsync(source, output);

        Assert.Equal(2, result.Documents);
        string text = await File.ReadAllTextAsync(output);
        Assert.Contains(CorpusMerger.DocumentSeparator, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_RequiresAnExistingSourceFolder()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => CorpusMerger.MergeAsync(temp.Combine("missing"), temp.Combine("out.txt")));
    }
}
