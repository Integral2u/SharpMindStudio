using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The held-out exam: train with an exam slice excluded, export the model, then
/// score that export on the same deterministic slice. This is also the shortest
/// proof that the exam can load a packaged model and its embedded tokenizer.
/// </summary>
public sealed class TrainingExamRunnerTests
{
    [Fact]
    public async Task Exam_ScoresTheExportedModelOnTheHeldOutSlice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-exam-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(dir, "corpus");
        Directory.CreateDirectory(corpus);
        try
        {
            for (int doc = 0; doc < 10; doc++)
            {
                string text = string.Join('\n', Enumerable.Range(0, 12)
                    .Select(line => $"exam document {doc} line {line}: the quick brown fox number {doc * 100 + line}"));
                await File.WriteAllTextAsync(Path.Combine(corpus, $"doc-{doc}.txt"), text);
            }

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);

            var job = new TrainingJobSettings
            {
                Name = "exam",
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
                TokenizerCachePath = Path.Combine(dir, "exam.tokenizer.json"),
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
                TotalSteps = 3,
                ValidationPercent = 25,
                TestPercent = 25,
                LogInterval = 1,
                BatchSize = 2,
                SeqLen = 8,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-4f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "exam.smm"),
                StartFresh = true,
            };

            var log = new List<string>();
            TrainingRunResult run = await TrainingRunner.RunAsync(
                job,
                plugins,
                new ListProgress<string>(log),
                new ListProgress<float>([]));

            Assert.True(run.Success, run.Error);
            Assert.Contains(log, line => line.Contains("promotion exam", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(run.ExportPath);

            TrainingExamResult exam = await TrainingExamRunner.RunAsync(
                job, run.ExportPath!, plugins, line => log.Add(line));

            Assert.True(double.IsFinite(exam.Loss));
            Assert.True(double.IsFinite(exam.Perplexity));
            Assert.True(exam.Batches > 0);
            Assert.True(exam.Tokens > 0);
            Assert.Equal(run.ExportPath, exam.ModelPath);
            Assert.Contains(log, line => line.Contains("Exam: loss", StringComparison.Ordinal));
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
