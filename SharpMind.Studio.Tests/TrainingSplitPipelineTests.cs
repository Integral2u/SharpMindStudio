using System.Text.Json;
using SharpMind.Data.Pipeline;
using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class TrainingSplitPipelineTests
{
    [Fact]
    public async Task TextSplitPipelines_KeepEveryFactoryVariantInOneSplit()
    {
        using var temp = new TempDirectory();
        string corpus = temp.Combine("factory-text.jsonl");
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
        await File.WriteAllLinesAsync(corpus, lines);

        string plugins = temp.Combine("plugins");
        Directory.CreateDirectory(plugins);

        var job = new TrainingJobSettings
        {
            Name = "split",
            Seed = seed,
            TestPercent = 25,
            ValidationPercent = 25,
            Sources =
            [
                new JobSource
                {
                    Component = new JobComponent
                    {
                        DisplayName = "Text JSONL",
                        TypeName = typeof(JsonlSource).FullName!,
                        Args = new Dictionary<string, string>
                        {
                            ["path"] = corpus,
                            ["textField"] = "text",
                        },
                    },
                },
            ],
        };

        int examThreshold = (int)Math.Round(0.25 * DeterministicSplit.Buckets);
        int valThreshold = (int)Math.Round(0.5 * DeterministicSplit.Buckets);
        var splits = new (string Name, int Low, int High)[]
        {
            ("exam", 0, examThreshold),
            ("val", examThreshold, valThreshold),
            ("train", valThreshold, DeterministicSplit.Buckets),
        };

        var seen = new Dictionary<int, HashSet<string>>();
        foreach ((string name, int low, int high) in splits)
        {
            PipelineNode pipeline = TrainingRunner.BuildCleanPipeline(
                job, plugins, new DeterministicSplitRange(low, high));

            int count = 0;
            await foreach (string text in pipeline.ReadAsync())
            {
                int document = byText[text];
                if (!seen.TryGetValue(document, out HashSet<string>? documentSplits))
                    seen[document] = documentSplits = [];
                documentSplits.Add(name);
                count++;
            }

            Assert.True(count > 0, $"the {name} split produced no documents");
        }

        Assert.Equal(documents, seen.Count);
        Assert.All(seen.Values, documentSplits => Assert.Single(documentSplits));
    }
}
