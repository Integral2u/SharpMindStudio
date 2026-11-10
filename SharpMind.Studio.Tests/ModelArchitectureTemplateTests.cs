using SharpMind.Model.Config;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ModelArchitectureTemplateTests
{
    [Fact]
    public void ApplyTo_WritesDimensionsAndLayerPlan()
    {
        var template = new ModelArchitectureTemplate
        {
            Name = "shape",
            ArchitecturePreset = "llama",
            HiddenDim = 384,
            NumLayers = 6,
            NumHeads = 6,
            NumKvHeads = 2,
            FfnDim = 1536,
            MaxSeqLen = 512,
            SlidingWindowSize = 128,
            LayerTypes = ["Full", "Sliding", "Sliding", "Full", "Sliding", "Full"],
        };
        var job = new TrainingJobSettings { TotalSteps = 77 };

        template.ApplyTo(job);

        Assert.Equal("shape", job.Name);
        Assert.Equal("llama", job.ArchitecturePreset);
        Assert.Equal(384, job.HiddenDim);
        Assert.Equal(6, job.NumLayers);
        Assert.Equal(6, job.NumHeads);
        Assert.Equal(2, job.NumKvHeads);
        Assert.Equal(1536, job.FfnDim);
        Assert.Equal(512, job.MaxSeqLen);
        Assert.Equal(128, job.LayerSlidingWindowSize);
        Assert.Equal(template.LayerTypes, job.LayerTypes);
        Assert.Equal(77, job.TotalSteps);
    }

    [Fact]
    public void ResolveModelConfig_AppliesSlidingLayerPlan()
    {
        var job = new TrainingJobSettings
        {
            HiddenDim = 64,
            NumLayers = 4,
            NumHeads = 4,
            NumKvHeads = 4,
            FfnDim = 128,
            MaxSeqLen = 256,
            LayerSlidingWindowSize = 32,
            LayerTypes = ["Full", "Sliding", "Full", "Sliding"],
        };

        ModelConfig config = TrainingModelOptions.ResolveModelConfig(job, vocabSize: 256);

        Assert.Equal(32, config.SlidingWindowSize);
        Assert.True(config.IsHybridSlidingWindow);
        Assert.Equal([false, true, false, true], Assert.IsType<bool[]>(config.SwaLayers));
        Assert.False(config.IsSwaLayer(0));
        Assert.True(config.IsSwaLayer(1));
        Assert.False(config.IsSwaLayer(2));
        Assert.True(config.IsSwaLayer(3));
    }

    [Fact]
    public void FromDescriptor_CarriesModelShapeIntoTemplate()
    {
        var descriptor = new ModelDescriptor
        {
            Path = "model.gguf",
            FileName = "model.gguf",
            SizeBytes = 1,
            LastWriteUtc = DateTimeOffset.UnixEpoch,
            DisplayName = "model",
            Family = new ModelFamily("llama", "LLaMA"),
            Architecture = "llama",
            ContextLength = 4096,
            NumLayers = 3,
            NumKvHeads = 2,
            HiddenDim = 256,
            NumHeads = 8,
            FfnDim = 1024,
            SlidingWindowSize = 128,
            SwaLayers = [false, true, false],
        };

        ModelArchitectureTemplate template = ModelArchitectureTemplate.FromDescriptor(descriptor);

        Assert.Equal("llama", template.ArchitecturePreset);
        Assert.Equal(256, template.HiddenDim);
        Assert.Equal(3, template.NumLayers);
        Assert.Equal(1024, template.FfnDim);
        Assert.Equal(4096, template.MaxSeqLen);
        Assert.Equal(["Full", "Sliding", "Full"], template.LayerTypes);
    }
}
