using SharpMind.Studio.Core.Chat;
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
        Assert.Equal("openai/gpt-4o-mini", document.RemoteModel);
        Assert.Equal("work", document.Mode);
        Assert.Equal("openai/gpt-4o-mini chat", document.Name);

        ChatSessionDocument reopened = store.Open(document.Id);
        Assert.Equal("openrouter", reopened.Provider);
        Assert.Equal("openai/gpt-4o-mini", reopened.RemoteModel);
        Assert.Equal("be kind", reopened.SystemPrompt);

        ChatSessionSummary summary = Assert.Single(store.List());
        Assert.Equal("openrouter", summary.Provider);
        Assert.Equal("openai/gpt-4o-mini", summary.RemoteModel);
    }
}
