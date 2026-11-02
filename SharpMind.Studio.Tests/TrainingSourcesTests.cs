using SharpMind.Data.Metadata;
using SharpMind.Data.Pipeline.Stages;
using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The source/stage editor builds components from the same metadata the runner
/// consumes. These tests pin the defaults and prove a job with several sources
/// plus a global stage still trains and exports.
/// </summary>
public sealed class TrainingSourcesTests
{
    [Fact]
    public void Factory_BuildsATextSourceAtItsDeclaredDefaults()
    {
        List<ComponentDescriptor> descriptors = ComponentRegistry.Scan(typeof(TextFileSource).Assembly);
        ComponentDescriptor text = descriptors.Single(d => d.Type == typeof(TextFileSource));

        JobComponent component = JobComponentFactory.Create(text);

        Assert.Equal(text.Type.AssemblyQualifiedName, component.TypeName);
        Assert.Equal("Text File", component.DisplayName);
        Assert.Equal("", component.Args["path"]);
        Assert.Equal("LinePerDoc", component.Args["mode"]);
        Assert.Same(text, JobComponentFactory.Find(component, descriptors));
    }

    [Fact]
    public async Task MultiSourceJobWithAGlobalStage_TrainsAndExports()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string first = Path.Combine(dir, "first.txt");
            string second = Path.Combine(dir, "second.txt");
            await File.WriteAllTextAsync(first, string.Join('\n', Enumerable.Repeat(
                "ALPHA source one: THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG", 120)));
            await File.WriteAllTextAsync(second, string.Join('\n', Enumerable.Repeat(
                "BETA source two: THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG", 120)));

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);

            List<ComponentDescriptor> descriptors = ComponentRegistry.Scan(typeof(TextFileSource).Assembly);
            ComponentDescriptor text = descriptors.Single(d => d.Type == typeof(TextFileSource));
            ComponentDescriptor lower = descriptors.Single(d => d.Type == typeof(LowerCase));

            JobComponent Source(string path)
            {
                JobComponent component = JobComponentFactory.Create(text);
                component.Args["path"] = path;
                component.Args["mode"] = "FilePerDoc";
                return component;
            }

            var job = new TrainingJobSettings
            {
                Name = "multi-source",
                Sources =
                [
                    new JobSource { Component = Source(first) },
                    new JobSource { Component = Source(second) },
                ],
                GlobalStages = [JobComponentFactory.Create(lower)],
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
                CheckpointInterval = 0,
                KeepRecent = -1,
                ExportPath = Path.Combine(dir, "multi-source.smm"),
                StartFresh = true,
            };

            var log = new List<string>();
            TrainingRunResult result = await TrainingRunner.RunAsync(
                job, plugins, new ListProgress<string>(log), new ListProgress<float>([]));

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(result.ExportPath));
            Assert.Contains(log, line => line.Contains("Sources:", StringComparison.Ordinal));
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
