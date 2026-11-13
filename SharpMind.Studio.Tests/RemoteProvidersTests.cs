using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class RemoteProvidersTests
{
    [Fact]
    public void EuroRouter_IsABetaRouterWithItsOwnBaseUrl()
    {
        RemoteProviderPreset preset = RemoteProviders.Find(RemoteProviders.EuroRouter);

        Assert.Equal("EuroRouter", preset.Name);
        Assert.Equal("https://api.eurouter.ai", preset.DefaultBaseUrl);
        Assert.Equal("https://www.eurouter.ai?ref=ZLSG4A2M", preset.SignupUrl);
        Assert.True(preset.Beta);
        Assert.False(preset.AttributionHeaders);
        Assert.Null(preset.DefaultModelId);
    }

    [Fact]
    public void OpenRouter_KeepsItsAttributionAndFreeDefault()
    {
        RemoteProviderPreset preset = RemoteProviders.Find(RemoteProviders.OpenRouter);

        Assert.True(preset.AttributionHeaders);
        Assert.False(preset.Beta);
        Assert.Equal("openrouter/free", preset.DefaultModelId);
    }

    [Fact]
    public void Find_FallsBackToOpenRouterForAnUnknownId()
    {
        Assert.Same(RemoteProviders.OpenRouterPreset, RemoteProviders.Find("nope"));
        Assert.Same(RemoteProviders.Default, RemoteProviders.Find(null));
    }
}
