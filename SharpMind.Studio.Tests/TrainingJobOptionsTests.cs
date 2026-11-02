using SharpMind.Data.Sources;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The full job editor writes every option into the .smmt, and the runner honours
/// the ones that change the model. These tests pin the round trip and one run
/// with a non-default preset, optimizer, QAT target and checkpoint policy.
/// </summary>
public sealed class TrainingJobOptionsTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsEveryEditorOption()
    {
        string path = Path.Combine(Path.GetTempPath(), "sharpmind-job-" + Guid.NewGuid().ToString("N") + ".smmt");
        try
        {
            var job = new TrainingJobSettings
            {
                Name = "options",
                ArchitecturePreset = "llama",
                Activation = "SiLU",
                Gate = "SwiGLU",
                Ffn = "Gated",
                Attention = "GQA",
                Norm = "RMSNorm",
                Arch = "Decoder",
                PositionalEncoding = "RoPE",
                HiddenDim = 96,
                NumLayers = 3,
                NumHeads = 6,
                NumKvHeads = 2,
                FfnDim = 256,
                MaxSeqLen = 192,
                NormEps = 2e-5f,
                Accelerator = "cpu-test",
                Optimizer = "SGD",
                SgdMomentum = 0.85f,
                QuantAwareTraining = "F16",
                TotalSteps = 77,
                LogInterval = 5,
                BatchSize = 3,
                SeqLen = 48,
                GradAccumSteps = 2,
                BatchingKind = "Packing",
                LearningRate = 4e-4f,
                MinLr = 2e-5f,
                WarmupSteps = 9,
                WeightDecay = 0.02f,
                GradClipNorm = 0.8f,
                LabelSmoothing = 0.05f,
                ValidationPercent = 12,
                TestPercent = 8,
                TokenizerKind = "Bpe",
                TokenizerVocabSize = 4096,
                TokenizerCachePath = @"C:\tmp\tokenizer.json",
                CheckpointInterval = 11,
                KeepRecent = 4,
                StartFresh = false,
                ResumeFrom = @"C:\tmp\checkpoints\step-0000022",
                WarmRestartFrom = @"C:\tmp\checkpoints\step-0000011",
                ExportPath = @"C:\tmp\options.smm",
                SystemPromptPath = @"C:\tmp\prompt.txt",
                SkillsFolder = @"C:\tmp\skills",
                PluginDllPaths = [@"C:\tmp\tools\a.dll", @"C:\tmp\tools\b.dll"],
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
                                ["path"] = @"C:\tmp\corpus.txt",
                                ["mode"] = "FilePerDoc",
                            },
                        },
                    },
                ],
            };

            Assert.True(job.Save(path, out string? saveError), saveError);
            TrainingJobSettings? loaded = TrainingJobSettings.Load(path, out string? loadError);

            Assert.NotNull(loaded);
            Assert.Null(loadError);
            Assert.Equal("options", loaded!.Name);
            Assert.Equal("llama", loaded.ArchitecturePreset);
            Assert.Equal("SiLU", loaded.Activation);
            Assert.Equal("SwiGLU", loaded.Gate);
            Assert.Equal("Gated", loaded.Ffn);
            Assert.Equal("GQA", loaded.Attention);
            Assert.Equal("RMSNorm", loaded.Norm);
            Assert.Equal("Decoder", loaded.Arch);
            Assert.Equal("RoPE", loaded.PositionalEncoding);
            Assert.Equal(96, loaded.HiddenDim);
            Assert.Equal(3, loaded.NumLayers);
            Assert.Equal(6, loaded.NumHeads);
            Assert.Equal(2, loaded.NumKvHeads);
            Assert.Equal(256, loaded.FfnDim);
            Assert.Equal(192, loaded.MaxSeqLen);
            Assert.Equal(2e-5f, loaded.NormEps);
            Assert.Equal("cpu-test", loaded.Accelerator);
            Assert.Equal("SGD", loaded.Optimizer);
            Assert.Equal(0.85f, loaded.SgdMomentum);
            Assert.Equal("F16", loaded.QuantAwareTraining);
            Assert.Equal(77, loaded.TotalSteps);
            Assert.Equal(5, loaded.LogInterval);
            Assert.Equal(3, loaded.BatchSize);
            Assert.Equal(48, loaded.SeqLen);
            Assert.Equal(2, loaded.GradAccumSteps);
            Assert.Equal("Packing", loaded.BatchingKind);
            Assert.Equal(4e-4f, loaded.LearningRate);
            Assert.Equal(2e-5f, loaded.MinLr);
            Assert.Equal(9, loaded.WarmupSteps);
            Assert.Equal(0.02f, loaded.WeightDecay);
            Assert.Equal(0.8f, loaded.GradClipNorm);
            Assert.Equal(0.05f, loaded.LabelSmoothing);
            Assert.Equal(12, loaded.ValidationPercent);
            Assert.Equal(8, loaded.TestPercent);
            Assert.Equal("Bpe", loaded.TokenizerKind);
            Assert.Equal(4096, loaded.TokenizerVocabSize);
            Assert.Equal(@"C:\tmp\tokenizer.json", loaded.TokenizerCachePath);
            Assert.Equal(11, loaded.CheckpointInterval);
            Assert.Equal(4, loaded.KeepRecent);
            Assert.False(loaded.StartFresh);
            Assert.Equal(@"C:\tmp\checkpoints\step-0000022", loaded.ResumeFrom);
            Assert.Equal(@"C:\tmp\checkpoints\step-0000011", loaded.WarmRestartFrom);
            Assert.Equal(@"C:\tmp\options.smm", loaded.ExportPath);
            Assert.Equal(@"C:\tmp\prompt.txt", loaded.SystemPromptPath);
            Assert.Equal(@"C:\tmp\skills", loaded.SkillsFolder);
            Assert.Equal([@"C:\tmp\tools\a.dll", @"C:\tmp\tools\b.dll"], loaded.PluginDllPaths);
            Assert.Equal(@"C:\tmp\corpus.txt", loaded.Sources[0].Component.Args["path"]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Runner_HonoursPresetSgdQatAndCheckpointPolicy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-options-" + Guid.NewGuid().ToString("N"));
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
                Name = "speedy-llama",
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
                TotalSteps = 2,
                LogInterval = 1,
                BatchSize = 2,
                SeqLen = 8,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-4f,
                WarmupSteps = 1,
                Optimizer = "SGD",
                SgdMomentum = 0.9f,
                QuantAwareTraining = "F16",
                CheckpointInterval = 1,
                KeepRecent = 1,
                TestPercent = 0,
                ValidationPercent = 0,
                ExportPath = Path.Combine(dir, "speedy-llama.smm"),
                StartFresh = true,
            };
            TrainingModelOptions.Apply(job, TrainingModelOptions.Presets.Single(p => p.ArchitecturePresetKey == "llama"));
            job.HiddenDim = 32;
            job.NumLayers = 1;
            job.NumHeads = 4;
            job.NumKvHeads = 2;
            job.FfnDim = 64;
            job.MaxSeqLen = 16;

            TrainingRunResult result = await TrainingRunner.RunAsync(
                job, plugins, new ListProgress<string>([]), new ListProgress<float>([]));

            Assert.True(result.Success, result.Error);
            Assert.True(TrainingModelOptions.UsesSgd(job));
            Assert.Equal("llama", job.ArchitecturePreset);

            string? checkpoint = Checkpoint.FindLatest(job.CheckpointDir);
            Assert.NotNull(checkpoint);
            Assert.True(File.Exists(Path.Combine(checkpoint!, "model.smm")));

            ModelConfig config = SmmLoader.LoadConfig(SmmLoader.LoadMeta(result.ExportPath!))!;
            Assert.Equal(32, config.HiddenDim);
            Assert.Equal(1, config.NumLayers);
            Assert.Equal(2, config.NumKvHeads);
            Assert.Equal(SharpMind.Model.Config.PositionalEncoding.RoPE, config.PositionalEncoding);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Validation_FlagsBlockingDimensionSplitAndQatProblems()
    {
        var job = new TrainingJobSettings
        {
            HiddenDim = 100,
            NumLayers = 2,
            NumHeads = 3,
            NumKvHeads = 3,
            FfnDim = 64,
            MaxSeqLen = 16,
            SeqLen = 8,
            TotalSteps = 10,
            ValidationPercent = 60,
            TestPercent = 40,
            QuantAwareTraining = "Q8_0",
        };

        IReadOnlyList<string> warnings = TrainingJobValidation.Warnings(job);

        Assert.Contains(warnings, w => w.Contains("divisible by NumHeads"));
        Assert.Contains(warnings, w => w.Contains("below 100%"));
        Assert.Contains(warnings, w => w.Contains("multiple of 32"));
    }

    [Fact]
    public void Validation_AcceptsAConsistentJob()
    {
        var job = new TrainingJobSettings
        {
            HiddenDim = 128,
            NumLayers = 4,
            NumHeads = 8,
            NumKvHeads = 8,
            FfnDim = 512,
            MaxSeqLen = 256,
            SeqLen = 64,
            TotalSteps = 200,
            ValidationPercent = 10,
            TestPercent = 10,
            QuantAwareTraining = "F16",
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
                            ["path"] = @"C:\tmp\corpus.txt",
                            ["mode"] = "FilePerDoc",
                        },
                    },
                },
            ],
        };

        Assert.Empty(TrainingJobValidation.Warnings(job));
    }

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value) => items.Add(value);
    }
}
