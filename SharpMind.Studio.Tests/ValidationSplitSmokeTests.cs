using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The held-out split: a job that asks for validation must remove a stable slice
/// of documents from the training stream, evaluate the fixed held-out batches,
/// and report the best step it saw.
/// </summary>
public sealed class ValidationSplitSmokeTests
{
    [Fact]
    public async Task ValidationSplit_EvaluatesHeldOutDocumentsAndTracksBestStep()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-validation-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(dir, "corpus");
        Directory.CreateDirectory(corpus);
        try
        {
            // Several documents with distinct content: the split is by document
            // hash, so one big file could land entirely on one side.
            for (int doc = 0; doc < 8; doc++)
            {
                string text = string.Join('\n', Enumerable.Range(0, 12)
                    .Select(line => $"document {doc} line {line}: the quick brown fox number {doc * 100 + line}"));
                await File.WriteAllTextAsync(Path.Combine(corpus, $"doc-{doc}.txt"), text);
            }

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);

            var job = new TrainingJobSettings
            {
                Name = "validation",
                Sources =
                [
                    new JobSource
                    {
                        Component = new JobComponent
                        {
                            DisplayName = "Text File",
                            TypeName = typeof(TextFileSource).FullName!,
                            Args = new Dictionary<string, string>
                            {
                                ["path"] = Path.Combine(corpus, "*.txt"),
                                ["mode"] = "FilePerDoc",
                            },
                        },
                    },
                ],
                TokenizerKind = "Char",
                TokenizerCachePath = Path.Combine(dir, "validation.tokenizer.json"),
                HiddenDim = 32,
                NumLayers = 1,
                NumHeads = 4,
                NumKvHeads = 4,
                FfnDim = 64,
                MaxSeqLen = 16,
                ArchitecturePreset = "gpt2",
                Activation = "GELU",
                Gate = "None",
                Ffn = "Dense",
                Attention = "MHA",
                Norm = "LayerNorm",
                Arch = "Decoder",
                PositionalEncoding = "RoPE",
                TotalSteps = 4,
                ValidationPercent = 50,
                LogInterval = 1,
                BatchSize = 2,
                SeqLen = 8,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-4f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "validation.smm"),
                StartFresh = true,
            };

            var log = new List<string>();
            TrainingRunResult result = await TrainingRunner.RunAsync(
                job,
                plugins,
                new ListProgress<string>(log),
                new ListProgress<float>([]));

            Assert.True(result.Success, result.Error);
            Assert.Contains(log, line => line.Contains("held out", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(log, line => line.Contains("Validation step", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(result.BestValLoss);
            Assert.True(double.IsFinite(result.BestValLoss!.Value));
            Assert.True(result.BestValStep is > 0 and <= 4, $"best step was {result.BestValStep}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value) => items.Add(value);
    }
}
