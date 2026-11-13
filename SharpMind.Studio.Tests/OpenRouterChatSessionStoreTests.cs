using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterChatSessionStoreTests
{
    [Fact]
    public void CreateRemote_RoundTripsProviderAndModel()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);

        ChatSessionDocument document = store.CreateRemote(
            "openai/gpt-4o-mini", "be kind", mode: "work");

        Assert.Equal("openrouter", document.Provider);
        Assert.Equal(RemoteProviders.OpenRouter, document.RemoteProvider);
        Assert.Equal("openai/gpt-4o-mini", document.RemoteModel);
        Assert.Equal("work", document.Mode);
        Assert.Equal("openai/gpt-4o-mini via OpenRouter", document.Name);

        ChatSessionDocument reopened = store.Open(document.Id);
        Assert.Equal("openrouter", reopened.Provider);
        Assert.Equal(RemoteProviders.OpenRouter, reopened.RemoteProvider);
        Assert.Equal("openai/gpt-4o-mini", reopened.RemoteModel);
        Assert.Equal("be kind", reopened.SystemPrompt);

        ChatSessionSummary summary = Assert.Single(store.List());
        Assert.Equal("openrouter", summary.Provider);
        Assert.Equal("openai/gpt-4o-mini", summary.RemoteModel);
    }

    [Fact]
    public void CreateRemote_RecordsTheEuroRouterProfile()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);

        ChatSessionDocument document = store.CreateRemote(
            "openai/gpt-4.1",
            systemPrompt: null,
            providerId: RemoteProviders.EuroRouter);

        Assert.Equal("openrouter", document.Provider);
        Assert.Equal(RemoteProviders.EuroRouter, document.RemoteProvider);
        Assert.Equal("EuroRouter", document.FamilyName);
        Assert.Equal("openai/gpt-4.1 via EuroRouter", document.Name);
        Assert.Equal(RemoteProviders.EuroRouter, store.Open(document.Id).RemoteProvider);
    }
}
