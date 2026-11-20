using SharpMind.Studio.Core.Inference;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class MtpProfileTests
{
    [Fact]
    public void Mtp_IsOnByDefaultWithTheEngineDraftLength()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(), null, null, null);

        Assert.True(value.MtpDraft);
        Assert.Null(value.MtpDraftTokens);
        Assert.Equal("System", value.Sources["MtpDraft"]);
    }

    [Fact]
    public void Model_CanSetTheDraftLength()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(),
            null,
            new InferenceOverrides { MtpDraftTokens = 6 },
            null);

        Assert.Equal(6, value.MtpDraftTokens);
        Assert.Equal("Model", value.Sources["MtpDraftTokens"]);
    }

    [Fact]
    public void Session_CanTurnMtpOff()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(),
            null,
            new InferenceOverrides { MtpDraftTokens = 6 },
            new InferenceOverrides { MtpDraft = false });

        Assert.False(value.MtpDraft);
        Assert.Equal("Session", value.Sources["MtpDraft"]);
        Assert.Equal(6, value.MtpDraftTokens);
    }

    [Fact]
    public void Family_OverrideSurvivesAnEmptyModelLayer()
    {
        ResolvedInference value = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(),
            new InferenceOverrides { MtpDraftTokens = 2 },
            new InferenceOverrides(),
            new InferenceOverrides());

        Assert.Equal(2, value.MtpDraftTokens);
        Assert.Equal("Family", value.Sources["MtpDraftTokens"]);
    }
}
