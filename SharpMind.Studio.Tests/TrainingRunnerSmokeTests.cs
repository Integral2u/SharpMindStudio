using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Proves the corpus-to-export training pipeline ported from the CUI runs inside
/// Studio.Core. Kept deliberately tiny: a character tokenizer, one layer and two
/// optimizer steps, so the test measures the wiring rather than the hardware.
/// </summary>
public sealed class TrainingRunnerSmokeTests
{
    [Fact]
    public async Task TinyCorpusRun_ExportsModel()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-training-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string corpus = Path.Combine(dir, "corpus.txt");
            await File.WriteAllTextAsync(
                corpus,
                string.Join('\n', Enumerable.Repeat(
                    "the quick brown fox jumps over the lazy dog and keeps running", 200)));

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);

            var job = new TrainingJobSettings
            {
                Name = "smoke",
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
                                ["path"] = corpus,
                                ["mode"] = "FilePerDoc",
                            },
                        },
                    },
                ],
                TokenizerKind = "Char",
                TokenizerCachePath = Path.Combine(dir, "smoke.tokenizer.json"),
                HiddenDim = 32,
                NumLayers = 2,
                NumHeads = 4,
                NumKvHeads = 4,
                FfnDim = 64,
                MaxSeqLen = 16,
                LayerTypes = ["Sliding", "Full"],
                LayerSlidingWindowSize = 8,
                ArchitecturePreset = "gpt2",
                Activation = "GELU",
                Gate = "None",
                Ffn = "Dense",
                Attention = "MHA",
                Norm = "LayerNorm",
                Arch = "Decoder",
                PositionalEncoding = "RoPE",
                TotalSteps = 2,
                LogInterval = 1,
                BatchSize = 2,
                SeqLen = 16,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-5f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "out"),
                StartFresh = true,
            };

            var log = new List<string>();
            var progress = new List<float>();
            TrainingRunResult result = await TrainingRunner.RunAsync(
                job,
                plugins,
                new ListProgress<string>(log),
                new ListProgress<float>(progress));

            Assert.True(
                result.Success,
                result.Error + Environment.NewLine + string.Join(Environment.NewLine, log.TakeLast(30)));
            Assert.NotNull(result.ExportPath);
            Assert.True(File.Exists(result.ExportPath));
            Assert.True(result.FinalStep > 0);
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
