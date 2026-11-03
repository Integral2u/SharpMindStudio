using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class TrainingPlaybookStoreTests
{
    [Fact]
    public void Save_ListApplyAndDeleteARoundTrips()
    {
        using var temp = new TempDirectory();
        var store = new TrainingPlaybookStore(temp.Path);

        TrainingJobSettings job = TrainingJobDefaults.Create();
        job.Name = "domain-128";
        job.HiddenDim = 128;
        job.TotalSteps = 42;

        SavedTrainingPlaybook saved = store.Save(
            SavedTrainingPlaybook.FromJob("My domain job", job));
        Assert.Equal("my-domain-job", saved.Id);
        Assert.Equal(TrainingPlaybookKind.Pretrain, saved.Kind);

        SavedTrainingPlaybook loaded = Assert.Single(store.List());
        Assert.Equal("My domain job", loaded.Name);

        TrainingPlaybook playbook = TrainingPlaybookStore.ToPlaybook(loaded);
        Assert.Equal("custom:my-domain-job", playbook.Id);
        Assert.Equal("Custom", playbook.Eyebrow);
        Assert.NotNull(playbook.CreateJob);

        TrainingJobSettings applied = playbook.CreateJob!();
        Assert.Equal(128, applied.HiddenDim);
        Assert.Equal(42, applied.TotalSteps);

        // The factory returns a clone, so edits in the editor do not mutate the file.
        applied.HiddenDim = 999;
        Assert.Equal(128, store.List()[0].Job!.HiddenDim);

        store.Delete(saved.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_KeepsSameNamedPlaybooksDistinct()
    {
        using var temp = new TempDirectory();
        var store = new TrainingPlaybookStore(temp.Path);
        TrainingJobSettings job = TrainingJobDefaults.Create();
        job.Name = "same";

        SavedTrainingPlaybook first = store.Save(SavedTrainingPlaybook.FromJob("Same name", job));
        SavedTrainingPlaybook second = store.Save(SavedTrainingPlaybook.FromJob("Same name", job));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void FineTunePlaybook_KeepsTheRecipe()
    {
        using var temp = new TempDirectory();
        var store = new TrainingPlaybookStore(temp.Path);
        var recipe = new TrainingFineTuneRecipe(
            Rank: 8,
            Alpha: 16,
            Targets: ["q_proj", "v_proj"],
            Steps: 60,
            LearningRate: 1e-4f,
            ValidationPercent: 10,
            TestPercent: 5,
            Output: "merged");

        SavedTrainingPlaybook saved = store.Save(
            SavedTrainingPlaybook.FromFineTune("Tiny LoRA", recipe, "base.smm"));
        TrainingPlaybook playbook = TrainingPlaybookStore.ToPlaybook(saved);

        Assert.Equal(TrainingPlaybookKind.FineTune, playbook.Kind);
        Assert.NotNull(playbook.FineTune);
        Assert.Equal(8, playbook.FineTune!.Rank);
        Assert.Equal(["q_proj", "v_proj"], playbook.FineTune.Targets);
        Assert.Equal("merged", playbook.FineTune.Output);
    }

    [Fact]
    public void CorruptFiles_AreSkipped()
    {
        using var temp = new TempDirectory();
        var store = new TrainingPlaybookStore(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "broken.json"), "{ not json");

        Assert.Empty(store.List());
    }
}
