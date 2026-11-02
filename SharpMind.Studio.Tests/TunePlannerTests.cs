using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Tests;

public sealed class TunePlannerTests
{
    private static ModelDescriptor Model(long sizeBytes = 512L * 1024 * 1024) => new()
    {
        Path = "model.gguf",
        FileName = "model.gguf",
        SizeBytes = sizeBytes,
        LastWriteUtc = DateTimeOffset.UtcNow,
        DisplayName = "model",
        Family = new ModelFamily("qwen2", "Qwen2"),
        ContextLength = 8192,
        NumLayers = 24,
        NumKvHeads = 8,
        HiddenDim = 1024,
        NumHeads = 16,
        KeyLength = 128,
        ValueLength = 128,
    };

    [Fact]
    public void WorkPreset_UsesTheToolSafeGenerator()
    {
        TuneRecommendation tune = TunePlanner.Recommend(
            Model(), TuneTarget.Work, TunePreset.Balanced, ["KVCacherBuilder", "Int8KVCacherBuilder"],
            totalMemoryOverride: 16L * 1024 * 1024 * 1024);

        Assert.Equal("StandardGeneratorBuilder", tune.Overrides.Generator);
        Assert.Equal("KVCacherBuilder", tune.Overrides.Cache);
        Assert.True(tune.Estimate.FitsFull);
        Assert.Contains(tune.Notes, note => note.Contains("StandardGeneratorBuilder"));
    }

    [Fact]
    public void OversizedModel_FallsBackToStreaming()
    {
        TuneRecommendation tune = TunePlanner.Recommend(
            Model(3L * 1024 * 1024 * 1024), TuneTarget.Chat, TunePreset.Balanced, ["KVCacherBuilder"],
            totalMemoryOverride: 2L * 1024 * 1024 * 1024);

        Assert.Equal("Streaming", tune.Overrides.LoadMode);
        Assert.False(tune.Estimate.FitsFull);
        Assert.Contains(tune.Notes, note => note.Contains("Streaming"));
    }

    [Fact]
    public void WorkPresets_RunColderThanChatForReliableToolCalls()
    {
        foreach (TunePreset preset in Enum.GetValues<TunePreset>())
        {
            TuneRecommendation work = TunePlanner.Recommend(
                Model(), TuneTarget.Work, preset, ["KVCacherBuilder"],
                totalMemoryOverride: 16L * 1024 * 1024 * 1024);
            TuneRecommendation chat = TunePlanner.Recommend(
                Model(), TuneTarget.Chat, preset, ["KVCacherBuilder"],
                totalMemoryOverride: 16L * 1024 * 1024 * 1024);

            Assert.True(work.Overrides.Temperature <= 0.2f, $"{preset} Work temperature");
            Assert.True(work.Overrides.Temperature < chat.Overrides.Temperature, $"{preset} Work vs Chat");
        }
    }

    [Fact]
    public void AccuracyPreset_UsesLowerTemperatureAndF32Cache()
    {
        TuneRecommendation tune = TunePlanner.Recommend(
            Model(), TuneTarget.Chat, TunePreset.Accuracy, ["KVCacherBuilder", "Int8KVCacherBuilder"],
            totalMemoryOverride: 16L * 1024 * 1024 * 1024);

        Assert.Equal("KVCacherBuilder", tune.Overrides.Cache);
        Assert.Equal(0.2f, tune.Overrides.Temperature);
        Assert.Equal(0.9f, tune.Overrides.TopP);
    }

    [Fact]
    public void ProfileSet_RequiresAllThreePresets()
    {
        var set = new TuneProfileSet
        {
            ModelFingerprint = "1:2",
            Profiles =
            [
                new TuneProfile { Target = TuneTarget.Chat, Preset = TunePreset.Performance },
                new TuneProfile { Target = TuneTarget.Chat, Preset = TunePreset.Balanced },
                new TuneProfile { Target = TuneTarget.Chat, Preset = TunePreset.Accuracy },
            ],
        };

        Assert.True(set.HasAll(TuneTarget.Chat));
        Assert.False(set.HasAll(TuneTarget.Work));
        Assert.NotNull(set.Find(TuneTarget.Chat, TunePreset.Accuracy));
    }

    [Fact]
    public void KvEstimate_UsesContextAndCacheWidth()
    {
        var model = Model();
        long f32 = TunePlanner.EstimateKvBytes(model, "KVCacherBuilder", 4096);
        long int8 = TunePlanner.EstimateKvBytes(model, "Int8KVCacherBuilder", 4096);

        Assert.True(f32 > int8);
        Assert.Equal(f32 / 4, int8);
    }
}

