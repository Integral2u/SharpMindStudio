using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// A rehearsal runs the real pipeline but must leave no model, checkpoint or
/// incremental state behind. This is the guard behind the planner's estimate.
/// </summary>
public sealed class TrainingRehearsalTests
{
    [Fact]
    public async Task Rehearsal_TrainsButExportsNothingAndLeavesTheJobFolderAlone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-rehearsal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string corpus = Path.Combine(dir, "corpus.txt");
            await File.WriteAllTextAsync(
                corpus,
                string.Join('\n', Enumerable.Repeat(
                    "the quick brown fox jumps over the lazy dog and keeps running", 150)));

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);

            string export = Path.Combine(dir, "should-not-exist.smm");
            var job = new TrainingJobSettings
            {
                Name = "rehearsal",
                Rehearsal = true,
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
                TokenizerCachePath = Path.Combine(dir, "tokenizer.json"),
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
                TotalSteps = 2,
                LogInterval = 1,
                BatchSize = 2,
                SeqLen = 8,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-4f,
                WarmupSteps = 1,
                CheckpointInterval = 1,
                KeepRecent = 1,
                ExportPath = export,
                StartFresh = true,
            };

            var log = new List<string>();
            TrainingRunResult result = await TrainingRunner.RunAsync(
                job, plugins, new ListProgress<string>(log), new ListProgress<float>([]));

            Assert.True(result.Success, result.Error);
            Assert.Null(result.ExportPath);
            Assert.False(File.Exists(export));
            Assert.False(Directory.Exists(job.CheckpointDir));
            Assert.Contains(log, line => line.Contains("Rehearsal finished", StringComparison.Ordinal));
            Assert.Equal(2, result.FinalStep);
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
