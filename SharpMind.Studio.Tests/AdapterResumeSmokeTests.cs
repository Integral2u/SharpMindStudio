using SharpMind.Data.Sources;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Adapter-only fine-tuning and its two resume paths:
///   * a portable .smlora file (weights only, fresh schedule),
///   * the per-job checkpoint folder (adapter sidecar + recorded step).
/// The base model is never rewritten in either case.
/// </summary>
public sealed class AdapterResumeSmokeTests
{
    [Fact]
    public async Task AdapterOnlyExport_ThenResumeFromTheAdapterFile()
    {
        using var fixture = await Fixture.CreateAsync();
        string adapterPath = Path.Combine(fixture.Directory, "tuned.smlora");
        string mergedPath = Path.Combine(fixture.Directory, "tuned.smm");

        var first = fixture.Job("tuned", mergedPath);
        first.MergeOnExport = false;
        first.SaveAdapterFile = true;
        first.AdapterPath = adapterPath;
        first.TotalSteps = 2;
        first.CheckpointInterval = 0;
        first.KeepRecent = -1;
        first.StartFresh = true;

        TrainingRunResult firstRun = await fixture.RunAsync(first);

        Assert.True(firstRun.Success, firstRun.Error);
        Assert.Null(firstRun.ExportPath);
        Assert.Equal(adapterPath, firstRun.AdapterPath);
        Assert.True(File.Exists(adapterPath));
        Assert.False(File.Exists(mergedPath), "an adapter-only run must not write a merged model");

        // Resume from the portable file. The job's rank and targets are
        // deliberately different: the adapter file defines both.
        var second = fixture.Job("tuned", mergedPath);
        second.ResumeFrom = adapterPath;
        second.StartFresh = false;
        second.LoRARank = 4;
        second.LoRATargetModules = ["q_proj"];
        second.TotalSteps = 2;
        second.CheckpointInterval = 0;
        second.KeepRecent = -1;
        second.SaveAdapterFile = true;
        second.AdapterPath = adapterPath;

        var log = new List<string>();
        TrainingRunResult secondRun = await fixture.RunAsync(second, log);

        Assert.True(secondRun.Success, secondRun.Error);
        Assert.Contains(log, line => line.Contains("resuming adapters", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log, line => line.Contains("the adapter file defines the targets", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(secondRun.ExportPath!));
        Assert.True(File.Exists(secondRun.AdapterPath!));
    }

    [Fact]
    public async Task CheckpointResume_ContinuesFromTheRecordedStep()
    {
        using var fixture = await Fixture.CreateAsync();
        string mergedPath = Path.Combine(fixture.Directory, "tuned.smm");

        var first = fixture.Job("tuned-ckpt", mergedPath);
        first.TotalSteps = 2;
        first.CheckpointInterval = 1;
        first.KeepRecent = 2;
        first.StartFresh = true;

        TrainingRunResult firstRun = await fixture.RunAsync(first);
        Assert.True(firstRun.Success, firstRun.Error);

        string? latest = Checkpoint.FindLatest(first.CheckpointDir);
        Assert.NotNull(latest);
        Assert.True(File.Exists(Path.Combine(latest!, "adapters.smlora")),
            "a fine-tune checkpoint must carry the portable adapter sidecar");

        var second = fixture.Job("tuned-ckpt", mergedPath);
        second.TotalSteps = 4;
        second.CheckpointInterval = 1;
        second.KeepRecent = 3;
        second.StartFresh = false;
        second.ResumeFrom = null;

        var log = new List<string>();
        TrainingRunResult secondRun = await fixture.RunAsync(second, log);

        Assert.True(secondRun.Success, secondRun.Error);
        Assert.Contains(log, line => line.Contains("resuming adapters from step 2", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, secondRun.FinalStep);
        Assert.True(File.Exists(secondRun.ExportPath!));
    }

    [Fact]
    public async Task Resume_RequiresMoreStepsThanTheCheckpointReached()
    {
        using var fixture = await Fixture.CreateAsync();
        string mergedPath = Path.Combine(fixture.Directory, "tuned.smm");

        var first = fixture.Job("tuned-guard", mergedPath);
        first.TotalSteps = 2;
        first.CheckpointInterval = 1;
        first.KeepRecent = 2;
        first.StartFresh = true;
        Assert.True((await fixture.RunAsync(first)).Success);

        var second = fixture.Job("tuned-guard", mergedPath);
        second.TotalSteps = 2;
        second.CheckpointInterval = 1;
        second.StartFresh = false;

        TrainingRunResult secondRun = await fixture.RunAsync(second);

        Assert.False(secondRun.Success);
        Assert.Contains("must be greater than the resumed adapter step", secondRun.Error);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; private init; } = "";
        public string Corpus { get; private init; } = "";
        public string Plugins { get; private init; } = "";
        public string BasePath { get; private init; } = "";

        public static async Task<Fixture> CreateAsync()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sharpmind-adapter-" + Guid.NewGuid().ToString("N"));
            string corpus = Path.Combine(dir, "corpus.txt");
            string plugins = Path.Combine(dir, "plugins");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.Directory.CreateDirectory(plugins);
            await File.WriteAllTextAsync(
                corpus,
                string.Join('\n', Enumerable.Repeat(
                    "the quick brown fox jumps over the lazy dog and keeps running", 200)));

            var baseJob = new TrainingJobSettings
            {
                Name = "adapter-base",
                Sources = [Source(corpus)],
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
                SeqLen = 8,
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
                baseJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]));
            Assert.True(baseRun.Success, baseRun.Error);

            return new Fixture
            {
                Directory = dir,
                Corpus = corpus,
                Plugins = plugins,
                BasePath = baseRun.ExportPath!,
            };
        }

        public TrainingJobSettings Job(string name, string export) => new()
        {
            Name = name,
            BaseModelPath = BasePath,
            Sources = [Source(Corpus)],
            LoRARank = 2,
            LoRAAlpha = 4f,
            LoRATargetModules = ["q_proj", "v_proj"],
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
            ExportPath = export,
            MergeOnExport = true,
            StartFresh = true,
        };

        public Task<TrainingRunResult> RunAsync(TrainingJobSettings job, List<string>? log = null)
            => TrainingRunner.RunAsync(
                job, Plugins,
                new ListProgress<string>(log ?? []),
                new ListProgress<float>([]));

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
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

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value) => items.Add(value);
    }
}
