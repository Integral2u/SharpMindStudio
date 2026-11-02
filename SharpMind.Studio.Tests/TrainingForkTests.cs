using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// A fork keeps the parent settings and data, points at the parent checkpoint
/// when one exists, and carries the lineage the Runs view shows.
/// </summary>
public sealed class TrainingForkTests
{
    [Fact]
    public void Fork_PretrainContinuesFromTheParentCheckpoint()
    {
        TrainingRunRecord parent = Parent(finalStep: 20);
        TrainingJobSettings job = PretrainJob("parent-model", totalSteps: 20);

        TrainingJobSettings fork = TrainingFork.Create(
            parent, job, @"C:\models\checkpoints\step-0000020", 20);

        Assert.Contains("fork", fork.Name);
        Assert.Null(fork.SourcePath);
        Assert.Null(fork.ExportPath);
        Assert.Empty(fork.SourceFileHashes);
        Assert.Equal(parent.Id, fork.ForkedFromRunId);
        Assert.Equal(parent.Id, fork.ForkRootId);
        Assert.Equal("step 20", fork.ForkPoint);
        Assert.Equal("fork", fork.Variant);
        Assert.Equal(@"C:\models\checkpoints\step-0000020", fork.WarmRestartFrom);
        Assert.Null(fork.ResumeFrom);
        Assert.False(fork.StartFresh);
        Assert.True(fork.TotalSteps > 20);
        Assert.Equal("parent-model", job.Name);
        Assert.Equal(20, job.TotalSteps);
    }

    [Fact]
    public void Fork_FineTuneResumesTheParentAdapters()
    {
        TrainingRunRecord parent = Parent(finalStep: 40);
        TrainingJobSettings job = PretrainJob("tuned", totalSteps: 40);
        job.BaseModelPath = @"C:\models\base.smm";
        job.ResumeFrom = null;

        TrainingJobSettings fork = TrainingFork.Create(
            parent, job, @"C:\models\checkpoints\step-0000040", 40);

        Assert.Equal(@"C:\models\checkpoints\step-0000040", fork.ResumeFrom);
        Assert.Null(fork.WarmRestartFrom);
        Assert.False(fork.StartFresh);
        Assert.True(fork.TotalSteps > 40);
        Assert.Equal(@"C:\models\base.smm", fork.BaseModelPath);
    }

    [Fact]
    public void Fork_WithoutACheckpointStartsFreshAtTheSameSettings()
    {
        TrainingRunRecord parent = Parent(finalStep: 10);
        TrainingJobSettings job = PretrainJob("plain", totalSteps: 10);

        TrainingJobSettings fork = TrainingFork.Create(parent, job);

        Assert.Null(fork.WarmRestartFrom);
        Assert.Null(fork.ResumeFrom);
        Assert.True(fork.StartFresh);
        Assert.Equal(10, fork.TotalSteps);
    }

    [Fact]
    public void Fork_OfAForkKeepsTheOriginalRoot()
    {
        TrainingRunRecord parent = Parent(finalStep: 5);
        parent.ForkRootId = "root-run";
        TrainingJobSettings job = PretrainJob("branch", totalSteps: 5);

        TrainingJobSettings fork = TrainingFork.Create(parent, job);

        Assert.Equal("root-run", fork.ForkRootId);
        Assert.Equal(parent.Id, fork.ForkedFromRunId);
    }

    private static TrainingRunRecord Parent(int finalStep) => new()
    {
        Id = "parent-run",
        Name = "parent",
        Success = true,
        FinalStep = finalStep,
        TotalSteps = finalStep,
    };

    private static TrainingJobSettings PretrainJob(string name, int totalSteps) => new()
    {
        Name = name,
        TotalSteps = totalSteps,
        SourcePath = @"C:\models\parent.smmt",
        ExportPath = @"C:\models\parent.smm",
        SourceFileHashes = new Dictionary<string, Dictionary<string, string>>
        {
            ["corpus.txt"] = new Dictionary<string, string> { ["hash"] = "abc" },
        },
        HiddenDim = 32,
        NumLayers = 1,
        NumHeads = 4,
        NumKvHeads = 4,
        FfnDim = 64,
        MaxSeqLen = 16,
        SeqLen = 8,
    };
}
