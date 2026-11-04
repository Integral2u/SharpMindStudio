using System.Security.Cryptography;
using SharpMind.Data.Sources;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;

namespace SharpMind.Studio.Tests;

/// <summary>
/// End-to-end LoRA fine-tune: train a tiny base from a corpus, load that .smm
/// back through <see cref="SmmTrainingPipeline.LoadForTraining"/>, adapt it,
/// merge, export, and prove the result loads for inference. Also proves the
/// base file is untouched (the fine-tune must be weight-only).
/// </summary>
public sealed class FineTuneRunnerSmokeTests
{
    [Fact]
    public async Task FineTuneBase_MergesAdaptersAndReloads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-finetune-" + Guid.NewGuid().ToString("N"));
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

            string promptPath = Path.Combine(dir, "system.txt");
            await File.WriteAllTextAsync(promptPath, "You are a test model.");
            string skillsDir = Path.Combine(dir, "skills");
            Directory.CreateDirectory(skillsDir);
            await File.WriteAllTextAsync(Path.Combine(skillsDir, "sample.md"), "# Sample skill");

            // 1. A real base model, exported by the same runner Studio uses.
            var baseJob = new TrainingJobSettings
            {
                Name = "base",
                Sources = [Source(corpus)],
                SystemPromptPath = promptPath,
                SkillsFolder = skillsDir,
                TokenizerKind = "Char",
                TokenizerCachePath = Path.Combine(dir, "base.tokenizer.json"),
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
                SeqLen = 16,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-5f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "base.smm"),
                StartFresh = true,
            };

            TrainingRunResult baseRun = await TrainingRunner.RunAsync(
                baseJob,
                plugins,
                new ListProgress<string>([]),
                new ListProgress<float>([]));

            Assert.True(baseRun.Success, baseRun.Error);
            string basePath = baseRun.ExportPath!;
            string baseHash = Hash(basePath);
            ModelConfig baseConfig = SmmLoader.LoadConfig(SmmLoader.LoadMeta(basePath))!;

            // 2. Fine-tune it with LoRA on the same corpus.
            var tuneJob = new TrainingJobSettings
            {
                Name = "tuned",
                BaseModelPath = basePath,
                Sources = [Source(corpus)],
                LoRARank = 2,
                LoRAAlpha = 4f,
                LoRATargetModules = ["q_proj", "v_proj", "up_proj", "down_proj"],
                TotalSteps = 2,
                LogInterval = 1,
                BatchSize = 1,
                SeqLen = 8,
                BatchingKind = "RandomWindow",
                LearningRate = 1e-3f,
                MinLr = 1e-5f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "tuned.smm"),
                StartFresh = true,
            };

            var tuneLog = new List<string>();
            TrainingRunResult tuneRun = await TrainingRunner.RunAsync(
                tuneJob,
                plugins,
                new ListProgress<string>(tuneLog),
                new ListProgress<float>([]));

            Assert.True(
                tuneRun.Success,
                tuneRun.Error + Environment.NewLine + string.Join(Environment.NewLine, tuneLog.TakeLast(30)));
            Assert.NotNull(tuneRun.ExportPath);
            Assert.True(File.Exists(tuneRun.ExportPath));
            Assert.Contains(tuneLog, line => line.Contains("adapters merged", StringComparison.OrdinalIgnoreCase));

            // The base is a frozen input: the tune must not rewrite it.
            Assert.Equal(baseHash, Hash(basePath));

            // The export is an ordinary model: same shape, same tokenizer, chat
            // metadata intact, and loadable by the inference path.
            ModelConfig tunedConfig = SmmLoader.LoadConfig(SmmLoader.LoadMeta(tuneRun.ExportPath!))!;
            Assert.Equal(baseConfig.VocabSize, tunedConfig.VocabSize);
            Assert.Equal(baseConfig.HiddenDim, tunedConfig.HiddenDim);
            Assert.Equal(baseConfig.NumLayers, tunedConfig.NumLayers);

            var lineage = SmmLoader.LoadLineage(tuneRun.ExportPath!);
            Assert.Equal("lora", lineage["method"]);
            Assert.Equal(basePath, lineage["base"]);
            Assert.Equal("2", lineage["lora_rank"]);

            // The base's embedded prompt and skills survive the merged export.
            Assert.Equal("You are a test model.", SmmLoader.LoadSystemPrompt(tuneRun.ExportPath!));
            Assert.Single(SmmLoader.LoadSkills(tuneRun.ExportPath!));

            using var reloaded = SmmTrainingPipeline.LoadForInference(
                tuneRun.ExportPath!, out var tokenizer, out _);
            var generated = SmmTrainingPipeline.GenerateGreedy(reloaded, [1, 4, 5], tunedConfig.VocabSize, steps: 3);
            Assert.Equal(6, generated.Count);
            Assert.All(generated, id => Assert.InRange(id, 0, tokenizer.VocabSize - 1));

            // 3. SFT over chat JSONL, on top of the same base. Loss is masked to
            // the assistant turns, and the export records the SFT method.
            string chats = Path.Combine(dir, "chats.jsonl");
            await File.WriteAllLinesAsync(chats,
            [
                """{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""",
                """{"prompt":"say hi","response":"hi there"}""",
                """{"instruction":"repeat","input":"the fox","output":"the fox runs"}""",
            ]);

            var sftJob = new TrainingJobSettings
            {
                Name = "sft",
                BaseModelPath = basePath,
                SftMode = true,
                SftFormatter = "simple",
                Sources = [ChatSource(chats)],
                LoRARank = 2,
                LoRAAlpha = 4f,
                LoRATargetModules = ["q_proj", "v_proj", "up_proj", "down_proj"],
                TotalSteps = 2,
                LogInterval = 1,
                BatchSize = 1,
                SeqLen = 16,
                LearningRate = 1e-3f,
                MinLr = 1e-5f,
                WarmupSteps = 1,
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "sft.smm"),
                StartFresh = true,
            };

            var sftLog = new List<string>();
            TrainingRunResult sftRun = await TrainingRunner.RunAsync(
                sftJob,
                plugins,
                new ListProgress<string>(sftLog),
                new ListProgress<float>([]));

            Assert.True(
                sftRun.Success,
                sftRun.Error + Environment.NewLine + string.Join(Environment.NewLine, sftLog.TakeLast(30)));
            Assert.True(File.Exists(sftRun.ExportPath));
            Assert.Contains(sftLog, line => line.Contains("SFT:", StringComparison.Ordinal));
            Assert.Contains(sftLog, line => line.Contains("assistant-only loss", StringComparison.Ordinal));
            Assert.Equal("sft", SmmLoader.LoadLineage(sftRun.ExportPath!)["method"]);
            Assert.Equal("simple", SmmLoader.LoadLineage(sftRun.ExportPath!)["sft_formatter"]);
            Assert.Equal(baseHash, Hash(basePath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static JobSource Source(string corpus) => new()
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
    };

    private static JobSource ChatSource(string corpus) => new()
    {
        Component = new JobComponent
        {
            DisplayName = "Chat JSONL",
            TypeName = typeof(JsonlSource).FullName!,
            Args = new Dictionary<string, string>
            {
                ["path"] = corpus,
            },
        },
    };

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value) => items.Add(value);
    }
}
