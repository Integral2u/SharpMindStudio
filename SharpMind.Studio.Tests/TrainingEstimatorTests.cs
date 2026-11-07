using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>Corpus size and pre-run estimates are size facts, not guesses.</summary>
public sealed class TrainingEstimatorTests
{
    [Fact]
    public void CorpusFacts_CountAGlobAndEstimateTokensFromBytes()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("a.txt"), new string('a', 400));
        File.WriteAllText(temp.Combine("b.txt"), new string('b', 800));

        var job = new TrainingJobSettings
        {
            Sources = [Source(Path.Combine(temp.Path, "*.txt"))],
        };

        TrainingCorpusEstimate estimate = TrainingCorpusFacts.Estimate(job);

        Assert.Equal(2, estimate.Files);
        Assert.Equal(1_200, estimate.Bytes);
        Assert.Equal(300, estimate.Tokens);
    }

    [Fact]
    public void CorpusFacts_CountAllFilesUnderADirectory()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("nested"));
        File.WriteAllText(temp.Combine("nested", "a.txt"), "abcd");

        var job = new TrainingJobSettings
        {
            Sources = [Source(temp.Path)],
        };

        TrainingCorpusEstimate estimate = TrainingCorpusFacts.Estimate(job);

        Assert.Equal(1, estimate.Files);
        Assert.Equal(4, estimate.Bytes);
    }

    [Fact]
    public void Estimator_ReportsParametersMemorySizeAndTokensPerParameter()
    {
        var job = new TrainingJobSettings
        {
            TokenizerKind = "Char",
            HiddenDim = 32,
            NumLayers = 1,
            NumHeads = 4,
            NumKvHeads = 4,
            FfnDim = 64,
            MaxSeqLen = 16,
            SeqLen = 8,
            BatchSize = 2,
            TotalSteps = 10,
            TokenizerVocabSize = 256,
        };

        TrainingEstimate estimate = TrainingEstimator.Estimate(
            job, new TrainingCorpusEstimate(2, 4_000, 1_000));

        Assert.NotNull(estimate.Parameters);
        Assert.NotNull(estimate.MemoryGiB);
        Assert.True(estimate.MemoryGiB > 0);
        Assert.NotNull(estimate.ModelSizeMb);
        Assert.True(estimate.ModelSizeMb > 0);
        Assert.Equal(1_000d / estimate.Parameters!.Value, estimate.TokensPerParameter!.Value, 6);
        Assert.Equal("F32", estimate.Precision);
    }

    [Fact]
    public void Estimator_WarnsWhenTokensPerParameterIsFarFromThePretrainingTarget()
    {
        var job = new TrainingJobSettings
        {
            TokenizerKind = "Char",
            HiddenDim = 32,
            NumLayers = 1,
            NumHeads = 4,
            NumKvHeads = 4,
            FfnDim = 64,
            MaxSeqLen = 16,
            SeqLen = 8,
            BatchSize = 2,
            TokenizerVocabSize = 256,
        };

        TrainingEstimate low = TrainingEstimator.Estimate(
            job, new TrainingCorpusEstimate(1, 400, 100));
        Assert.NotEmpty(low.Warnings);

        TrainingEstimate high = TrainingEstimator.Estimate(
            job, new TrainingCorpusEstimate(1, 40_000_000, 10_000_000));
        Assert.NotEmpty(high.Warnings);

        job.SftMode = true;
        job.BaseModelPath = @"C:\models\base.smm";
        TrainingEstimate sft = TrainingEstimator.Estimate(
            job, new TrainingCorpusEstimate(1, 400, 100));
        Assert.Empty(sft.Warnings);
    }

    [Fact]
    public void Job_DefaultsToTheLegacySeed()
        => Assert.Equal(TrainingRunner.Seed, new TrainingJobSettings().Seed);

    [Theory]
    [InlineData(null, 4.0)]
    [InlineData("F32", 4.0)]
    [InlineData("F16", 2.0)]
    [InlineData("Q8_0", 1.0625)]
    [InlineData("Q4_0", 0.5625)]
    [InlineData("Q4_K", 0.5625)]
    public void BytesPerParameter_MatchesThePackagedPrecision(string? qat, double expected)
        => Assert.Equal(expected, TrainingEstimator.BytesPerParameter(qat));

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
}
