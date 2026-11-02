using SharpMind.Data.Sources;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Content addressing must be stable for identical recipe inputs, change when a
/// training input changes, and allow the runner to reuse an exported model.
/// </summary>
public sealed class TrainingRecipeTests
{
    [Fact]
    public void Hashes_AreStableAndSensitiveToTheRightInputs()
    {
        TrainingJobSettings job = Job("model", @"C:\data\corpus.txt", learningRate: 3e-4f);
        string data = TrainingRecipeHash.Data(job);
        string tokenizer = TrainingRecipeHash.Tokenizer(job, data);
        string train = TrainingRecipeHash.Train(job, data, tokenizer);

        TrainingJobSettings same = Job("another-name", @"C:\data\corpus.txt", learningRate: 3e-4f);
        same.ExportPath = @"C:\other\model.smm";
        same.SourcePath = @"C:\other\job.smmt";
        Assert.Equal(data, TrainingRecipeHash.Data(same));
        Assert.Equal(train, TrainingRecipeHash.Train(
            same, TrainingRecipeHash.Data(same), TrainingRecipeHash.Tokenizer(same, data)));

        TrainingJobSettings otherData = Job("model", @"C:\data\other.txt", learningRate: 3e-4f);
        Assert.NotEqual(data, TrainingRecipeHash.Data(otherData));

        TrainingJobSettings otherLr = Job("model", @"C:\data\corpus.txt", learningRate: 9e-4f);
        Assert.Equal(data, TrainingRecipeHash.Data(otherLr));
        Assert.NotEqual(train, TrainingRecipeHash.Train(
            otherLr, data, TrainingRecipeHash.Tokenizer(otherLr, data)));

        TrainingJobSettings otherVocab = Job("model", @"C:\data\corpus.txt", learningRate: 3e-4f);
        otherVocab.TokenizerVocabSize += 512;
        Assert.NotEqual(tokenizer, TrainingRecipeHash.Tokenizer(otherVocab, data));
    }

    [Fact]
    public void Store_RoundTripsAndRejectsAMissingArtifact()
    {
        using var temp = new TempDirectory();
        string artifact = temp.Combine("model.smm");
        File.WriteAllBytes(artifact, [1, 2, 3]);
        string recipes = temp.Combine("recipes");

        TrainingRecipeStore.Save(new TrainingRecipeArtifact(
            "train", "abc123", artifact, "job", DateTime.UtcNow), recipes);

        Assert.True(TrainingRecipeStore.TryGet("train", "abc123", out TrainingRecipeArtifact found, recipes));
        Assert.Equal(Path.GetFullPath(artifact), found.ArtifactPath);

        File.Delete(artifact);
        Assert.False(TrainingRecipeStore.TryGet("train", "abc123", out _, recipes));
        Assert.False(TrainingRecipeStore.TryGet("train", "missing", out _, recipes));
    }

    [Fact]
    public async Task Runner_ReusesAnIdenticalRecipe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-recipe-" + Guid.NewGuid().ToString("N"));
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

            TrainingJobSettings job = SmallJob("recipe", corpus, Path.Combine(dir, "recipe.smm"));
            string recipes = Path.Combine(dir, "recipes");

            TrainingRunResult first = await TrainingRunner.RunAsync(
                job, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
                recipeFolder: recipes);
            Assert.True(first.Success, first.Error);
            Assert.False(first.Cached);
            Assert.NotNull(first.ExportPath);
            Assert.True(File.Exists(first.ExportPath));

            TrainingRunResult second = await TrainingRunner.RunAsync(
                job, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
                recipeFolder: recipes);

            Assert.True(second.Success, second.Error);
            Assert.True(second.Cached, "an identical recipe should hit the model cache");
            Assert.Equal(first.ExportPath, second.ExportPath);
            Assert.Equal(first.RecipeHash, second.RecipeHash);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static TrainingJobSettings Job(string name, string corpus, float learningRate) => new()
    {
        Name = name,
        Sources = [Source(corpus)],
        TokenizerKind = "Bpe",
        TokenizerVocabSize = 512,
        HiddenDim = 32,
        NumLayers = 1,
        NumHeads = 4,
        NumKvHeads = 4,
        FfnDim = 64,
        MaxSeqLen = 16,
        SeqLen = 8,
        BatchSize = 2,
        TotalSteps = 2,
        BatchingKind = "RandomWindow",
        LearningRate = learningRate,
        MinLr = 1e-4f,
        WarmupSteps = 1,
        CheckpointInterval = 0,
        KeepRecent = -1,
        ExportPath = @"C:\models\model.smm",
        StartFresh = true,
    };

    private static TrainingJobSettings SmallJob(string name, string corpus, string export)
    {
        TrainingJobSettings job = Job(name, corpus, 1e-3f);
        job.TokenizerKind = "Char";
        job.TokenizerVocabSize = 256;
        job.ExportPath = export;
        job.TokenizerCachePath = Path.Combine(Path.GetDirectoryName(export)!, "tokenizer.json");
        return job;
    }

    private static JobSource Source(string path) => new()
    {
        Component = new JobComponent
        {
            DisplayName = "Text File",
            TypeName = typeof(TextFileSource).FullName!,
            Args = new Dictionary<string, string>
            {
                ["path"] = path,
                ["mode"] = "FilePerDoc",
            },
        },
    };

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value) => items.Add(value);
    }
}
