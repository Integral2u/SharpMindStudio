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
        Assert.Equal("EUR", preset.Currency);
    }

    [Fact]
    public void OpenRouter_KeepsItsAttributionAndFreeDefault()
    {
        RemoteProviderPreset preset = RemoteProviders.Find(RemoteProviders.OpenRouter);

        Assert.True(preset.AttributionHeaders);
        Assert.False(preset.Beta);
        Assert.Equal("openrouter/free", preset.DefaultModelId);
        Assert.Equal("USD", preset.Currency);
        Assert.Equal(ReasoningRequestStyle.OpenRouter, preset.ReasoningStyle);
    }

    [Fact]
    public void OpenCodeGo_UsesItsGatewayAndSessionHeader()
    {
        RemoteProviderPreset preset = RemoteProviders.Find(RemoteProviders.OpenCodeGo);

        Assert.Equal("OpenCode Go", preset.Name);
        Assert.Equal(
            "https://opencode.ai/inference/go/openai/v1",
            preset.DefaultBaseUrl);
        Assert.Equal("https://opencode.ai/console", preset.SignupUrl);
        Assert.False(preset.Beta);
        Assert.False(preset.AttributionHeaders);
        Assert.Equal("deepseek-v4.1-flash", preset.DefaultModelId);
        Assert.Equal("x-opencode-session", preset.SessionHeader);
        Assert.True(preset.KeyTestUsesModels);
        Assert.Equal("USD", preset.Currency);
        Assert.Equal(ReasoningRequestStyle.OpenAiEffort, preset.ReasoningStyle);
    }

    [Fact]
    public void All_KeepsOpenRouterFirstForExistingDefaults()
    {
        Assert.Same(RemoteProviders.OpenRouterPreset, RemoteProviders.All[0]);
        Assert.Contains(
            RemoteProviders.All,
            preset => preset.Id == RemoteProviders.OpenCodeGo);
    }

    [Fact]
    public void IsRemote_RecognizesEveryPresetButNotLocalValues()
    {
        Assert.True(RemoteProviders.IsRemote(RemoteProviders.OpenRouter));
        Assert.True(RemoteProviders.IsRemote(RemoteProviders.EuroRouter));
        Assert.True(RemoteProviders.IsRemote(RemoteProviders.OpenCodeGo));
        Assert.True(RemoteProviders.IsRemote("OPENCODE-GO"));
        Assert.False(RemoteProviders.IsRemote("local"));
        Assert.False(RemoteProviders.IsRemote("compute:compute-1"));
        Assert.False(RemoteProviders.IsRemote(null));
        Assert.False(RemoteProviders.IsRemote(""));
    }

    [Fact]
    public void Find_FallsBackToOpenRouterForAnUnknownId()
    {
        Assert.Same(RemoteProviders.OpenRouterPreset, RemoteProviders.Find("nope"));
        Assert.Same(RemoteProviders.Default, RemoteProviders.Find(null));
    }
}
