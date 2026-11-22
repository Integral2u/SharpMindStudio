using SharpMind.Inference;
using SharpMind.Studio.Core.Inference;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class MedusaProfileTests
{
    [Fact]
    public void Medusa_DefaultsToThreeHeadsAndNoCalibration()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(), null, null, null);

        Assert.Equal(3, value.MedusaNumHeads);
        Assert.Equal(0, value.MedusaCalibrationSamples);
        Assert.Equal(20, value.MedusaCalibrationSteps);
        Assert.Equal(0.01f, value.MedusaCalibrationLearningRate);
        Assert.Null(value.MedusaHeadsPath);
        Assert.Equal("System", value.Sources["MedusaNumHeads"]);
    }

    [Fact]
    public void Model_CanPointAtASidecarAndCalibrationPrompt()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(),
            null,
            new InferenceOverrides
            {
                MedusaNumHeads = 4,
                MedusaCalibrationSamples = 32,
                MedusaCalibrationSteps = 10,
                MedusaCalibrationLearningRate = 0.02f,
                MedusaCalibrationPrompt = "abc",
                MedusaHeadsPath = @"C:\heads\model-h4.mdh",
                MedusaFingerprint = "model:123",
            },
            null);

        Assert.Equal(4, value.MedusaNumHeads);
        Assert.Equal(32, value.MedusaCalibrationSamples);
        Assert.Equal(10, value.MedusaCalibrationSteps);
        Assert.Equal(0.02f, value.MedusaCalibrationLearningRate);
        Assert.Equal("abc", value.MedusaCalibrationPrompt);
        Assert.Equal(@"C:\heads\model-h4.mdh", value.MedusaHeadsPath);
        Assert.Equal("model:123", value.MedusaFingerprint);
        Assert.Equal("Model", value.Sources["MedusaHeadsPath"]);
    }

    [Fact]
    public void Session_OverridesHeadsWithoutLosingThePath()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(),
            null,
            new InferenceOverrides { MedusaHeadsPath = "heads.mdh", MedusaNumHeads = 4 },
            new InferenceOverrides { MedusaNumHeads = 2 });

        Assert.Equal(2, value.MedusaNumHeads);
        Assert.Equal("heads.mdh", value.MedusaHeadsPath);
        Assert.Equal("Session", value.Sources["MedusaNumHeads"]);
        Assert.Equal("Model", value.Sources["MedusaHeadsPath"]);
    }

    [Fact]
    public void DelegateConfigurator_ReceivesTheBuilderInstance()
    {
        object? seen = null;
        var configurator = new DelegateBuilderConfigurator(builder => seen = builder);
        var target = new object();

        configurator.Configure(target);

        Assert.Same(target, seen);
    }
}
