using System.Text.Json;
using SharpMind.Data.Pipeline;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SplitSourcesTests
{
    [Fact]
    public async Task GroupedJsonlSource_KeepsEveryVariantOfADocumentInOneSplit()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("factory-text.jsonl");
        int seed = 1234;
        int documents = 40;
        int variants = 3;

        var byText = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<string>();
        for (int document = 0; document < documents; document++)
        {
            for (int variant = 0; variant < variants; variant++)
            {
                string text = $"document {document:D3} variant {variant} " +
                    "the quick brown fox jumps over the lazy dog";
                byText[text] = document;
                lines.Add(JsonSerializer.Serialize(new
                {
                    text,
                    source = "factory-corpus.txt",
                    id = document,
                    variant,
                }));
            }
        }
        await File.WriteAllLinesAsync(path, lines);

        int examThreshold = DeterministicSplit.Buckets / 4;
        int valThreshold = DeterministicSplit.Buckets / 2;

        var splits = new Dictionary<int, HashSet<int>>();
        foreach ((string name, int low, int high) in new[]
        {
            ("exam", 0, examThreshold),
            ("val", examThreshold, valThreshold),
            ("train", valThreshold, DeterministicSplit.Buckets),
        })
        {
            var source = new GroupedJsonlSource(path, "text", seed, low, high);
            var seenTexts = new HashSet<string>(StringComparer.Ordinal);
            await foreach (string text in source.ReadAsync())
            {
                int document = byText[text];
                Assert.True(seenTexts.Add(text),
                    $"text appeared twice in the {name} split");
                if (!splits.TryGetValue(document, out HashSet<int>? bucket))
                    splits[document] = bucket = [];
                bucket.Add(low);
            }
        }

        Assert.Equal(documents, splits.Count);
        Assert.All(splits.Values, bucket => Assert.Single(bucket));
        Assert.Equal(lines.Count, splits.Values.Sum(bucket => variants));
    }

    [Fact]
    public async Task GroupedJsonlSource_ReturnsOnlyTheRequestedBucketRange()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("one.jsonl");
        int seed = 77;
        string text = "a single document";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            text,
            source = "one.txt",
            id = 1,
        }));

        int bucket = DeterministicSplit.Bucket("one.txt#1", seed);
        var inside = new GroupedJsonlSource(path, "text", seed, bucket, bucket + 1);
        var outside = new GroupedJsonlSource(path, "text", seed, bucket + 1, bucket + 2);

        Assert.Equal(new[] { text }, await ReadAll(inside));
        Assert.Empty(await ReadAll(outside));
    }

    [Fact]
    public async Task GroupedJsonlSource_FallsBackToTheTextWhenMetadataIsMissing()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("plain.jsonl");
        int seed = 11;
        string text = "no source or id";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { text }));

        int bucket = DeterministicSplit.Bucket(text, seed);
        var inside = new GroupedJsonlSource(path, "text", seed, bucket, bucket + 1);
        var outside = new GroupedJsonlSource(path, "text", seed, bucket + 1, bucket + 2);

        Assert.Equal(new[] { text }, await ReadAll(inside));
        Assert.Empty(await ReadAll(outside));
    }

    private static async Task<List<string>> ReadAll(GroupedJsonlSource source)
    {
        var items = new List<string>();
        await foreach (string text in source.ReadAsync())
            items.Add(text);
        return items;
    }
}
