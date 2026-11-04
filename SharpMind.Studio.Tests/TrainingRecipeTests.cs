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
    public void Data_ChangesWhenSftModeOrFormatterChanges()
    {
        TrainingJobSettings job = Job("model", @"C:\data\corpus.txt", learningRate: 3e-4f);
        string data = TrainingRecipeHash.Data(job);

        job.SftMode = true;
        string sftData = TrainingRecipeHash.Data(job);
        Assert.NotEqual(data, sftData);

        job.SftFormatter = "chatml";
        Assert.NotEqual(sftData, TrainingRecipeHash.Data(job));
    }

    [Fact]
    public void TrainBase_IgnoresTheHorizonAndPackageButKeepsOptimizerInputs()
    {
        TrainingJobSettings job = Job("model", @"C:\data\corpus.txt", learningRate: 3e-4f);
        string data = TrainingRecipeHash.Data(job);
        string tokenizer = TrainingRecipeHash.Tokenizer(job, data);
        string baseHash = TrainingRecipeHash.TrainBase(job, data, tokenizer);

        TrainingJobSettings longer = Job("another-name", @"C:\data\corpus.txt", learningRate: 3e-4f);
        longer.TotalSteps = 50;
        longer.LogInterval = 1;
        longer.CheckpointInterval = 1;
        longer.ExportPath = @"C:\models\other.smm";
        longer.SystemPromptPath = @"C:\prompts\other.txt";
        Assert.Equal(baseHash, TrainingRecipeHash.TrainBase(
            longer, TrainingRecipeHash.Data(longer), TrainingRecipeHash.Tokenizer(longer, data)));

        TrainingJobSettings faster = Job("model", @"C:\data\corpus.txt", learningRate: 9e-4f);
        Assert.NotEqual(baseHash, TrainingRecipeHash.TrainBase(
            faster, data, TrainingRecipeHash.Tokenizer(faster, data)));
    }

    [Fact]
    public void Train_HashesPackageContentsButTrainBaseDoesNot()
    {
        using var temp = new TempDirectory();
        string prompt = temp.Combine("prompt.txt");
        File.WriteAllText(prompt, "first prompt");

        TrainingJobSettings job = Job("model", @"C:\data\corpus.txt", learningRate: 3e-4f);
        job.SystemPromptPath = prompt;
        string data = TrainingRecipeHash.Data(job);
        string tokenizer = TrainingRecipeHash.Tokenizer(job, data);
        string train = TrainingRecipeHash.Train(job, data, tokenizer);
        string baseHash = TrainingRecipeHash.TrainBase(job, data, tokenizer);

        File.WriteAllText(prompt, "second prompt");

        Assert.NotEqual(train, TrainingRecipeHash.Train(job, data, tokenizer));
        Assert.Equal(baseHash, TrainingRecipeHash.TrainBase(job, data, tokenizer));
    }

    [Fact]
    public void Data_ChangesWhenSourceContentChanges()
    {
        using var temp = new TempDirectory();
        string corpus = temp.Combine("corpus.txt");
        File.WriteAllText(corpus, "first version of the corpus");
        TrainingJobSettings job = Job("model", corpus, learningRate: 3e-4f);

        string first = TrainingRecipeHash.Data(job);
        File.WriteAllText(corpus, "second version of the corpus");

        Assert.NotEqual(first, TrainingRecipeHash.Data(job));
    }

    [Fact]
    public async Task Runner_ReusesTheDataPhaseWhenOnlyTrainingChanges()
    {
        using var temp = new TempDirectory();
        string corpus = temp.Combine("corpus.txt");
        await File.WriteAllTextAsync(
            corpus,
            string.Join('\n', Enumerable.Repeat(
                "the quick brown fox jumps over the lazy dog and keeps running", 150)));
        string plugins = temp.Combine("plugins");
        Directory.CreateDirectory(plugins);
        string recipes = temp.Combine("recipes");

        TrainingJobSettings firstJob = SmallJob("data-one", corpus, temp.Combine("data-one.smm"));
        var firstLog = new List<string>();
        TrainingRunResult first = await TrainingRunner.RunAsync(
            firstJob, plugins, new ListProgress<string>(firstLog), new ListProgress<float>([]),
            recipeFolder: recipes);
        Assert.True(first.Success, first.Error);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(recipes, "data"), "*.jsonl"));

        TrainingJobSettings secondJob = SmallJob("data-two", corpus, temp.Combine("data-two.smm"));
        secondJob.LearningRate = 9e-4f;
        var secondLog = new List<string>();
        TrainingRunResult second = await TrainingRunner.RunAsync(
            secondJob, plugins, new ListProgress<string>(secondLog), new ListProgress<float>([]),
            recipeFolder: recipes);

        Assert.True(second.Success, second.Error);
        Assert.Contains(secondLog,
            line => line.Contains("reused data phase", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task Runner_CopiesACachedModelToANewExportPath()
    {
        using var temp = new TempDirectory();
        string corpus = temp.Combine("corpus.txt");
        await File.WriteAllTextAsync(
            corpus,
            string.Join('\n', Enumerable.Repeat(
                "the quick brown fox jumps over the lazy dog and keeps running", 150)));
        string plugins = temp.Combine("plugins");
        Directory.CreateDirectory(plugins);
        string recipes = temp.Combine("recipes");

        TrainingJobSettings firstJob = SmallJob("recipe-one", corpus, temp.Combine("one.smm"));
        TrainingRunResult first = await TrainingRunner.RunAsync(
            firstJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
            recipeFolder: recipes);
        Assert.True(first.Success, first.Error);

        TrainingJobSettings movedJob = SmallJob("recipe-two", corpus, temp.Combine("two.smm"));
        TrainingRunResult moved = await TrainingRunner.RunAsync(
            movedJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
            recipeFolder: recipes);

        Assert.True(moved.Success, moved.Error);
        Assert.True(moved.Cached);
        Assert.Equal("package", moved.CachedPhase);
        Assert.Equal(System.IO.Path.GetFullPath(temp.Combine("two.smm")),
            System.IO.Path.GetFullPath(moved.ExportPath!));
        Assert.True(File.Exists(moved.ExportPath));
        Assert.True(File.Exists(first.ExportPath));
    }

    [Fact]
    public async Task Runner_ReusesCheckpointsForLongerAndAlreadyCoveredRuns()
    {
        using var temp = new TempDirectory();
        string corpus = temp.Combine("corpus.txt");
        await File.WriteAllTextAsync(
            corpus,
            string.Join('\n', Enumerable.Repeat(
                "the quick brown fox jumps over the lazy dog and keeps running", 150)));
        string plugins = temp.Combine("plugins");
        Directory.CreateDirectory(plugins);
        string recipes = temp.Combine("recipes");

        TrainingJobSettings firstJob = SmallJob("recipe-step-one", corpus, temp.Combine("step-one.smm"));
        firstJob.TotalSteps = 1;
        firstJob.CheckpointInterval = 1;
        firstJob.StartFresh = false;
        TrainingRunResult first = await TrainingRunner.RunAsync(
            firstJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
            recipeFolder: recipes);
        Assert.True(first.Success, first.Error);
        Assert.Equal(1, first.FinalStep);

        TrainingJobSettings secondJob = SmallJob("recipe-step-two", corpus, temp.Combine("step-two.smm"));
        secondJob.TotalSteps = 3;
        secondJob.CheckpointInterval = 1;
        secondJob.StartFresh = false;
        TrainingRunResult second = await TrainingRunner.RunAsync(
            secondJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
            recipeFolder: recipes);
        Assert.True(second.Success, second.Error);
        Assert.True(second.Cached, "a cached checkpoint should continue the longer run");
        Assert.Equal("checkpoint", second.CachedPhase);
        Assert.Equal(3, second.FinalStep);

        TrainingJobSettings thirdJob = SmallJob("recipe-step-three", corpus, temp.Combine("step-three.smm"));
        thirdJob.TotalSteps = 2;
        thirdJob.CheckpointInterval = 1;
        thirdJob.StartFresh = false;
        TrainingRunResult third = await TrainingRunner.RunAsync(
            thirdJob, plugins, new ListProgress<string>([]), new ListProgress<float>([]),
            recipeFolder: recipes);
        Assert.True(third.Success, third.Error);
        Assert.True(third.Cached, "a checkpoint at or past the horizon should be exported as-is");
        Assert.Equal("checkpoint", third.CachedPhase);
        Assert.Equal(3, third.FinalStep);
        Assert.True(File.Exists(third.ExportPath));
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
