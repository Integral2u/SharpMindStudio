using SharpMind.Inference.Chat;
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

    [Fact]
    public void SwitchLocalModel_KeepsMessagesAndUpdatesTheDocument()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);
        ChatSessionDocument document = store.Create(
            @"C:\models\a.gguf", "family-a", "Family A", "1.0", "be kind");
        document.Messages.Add(ChatMessage.User("hello"));
        document.Messages.Add(ChatMessage.Agent("hi"));
        store.Save(document);

        document.ModelPath = @"C:\models\b.gguf";
        document.FamilyId = "family-b";
        document.FamilyName = "Family B";
        document.ModelFingerprint = "size:2";
        document.Overrides = document.Overrides with { Temperature = 0.2f };
        store.Save(document);

        ChatSessionDocument reopened = store.Open(document.Id);
        Assert.Equal(@"C:\models\b.gguf", reopened.ModelPath);
        Assert.Equal("family-b", reopened.FamilyId);
        Assert.Equal("Family B", reopened.FamilyName);
        Assert.Equal(2, reopened.Messages.Count);
        Assert.Equal("hello", reopened.Messages[0].Content);
        Assert.Equal(0.2f, reopened.Overrides.Temperature);
    }

    [Fact]
    public void SwitchRemoteModel_KeepsMessagesAndTheProvider()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);
        ChatSessionDocument document = store.CreateRemote(
            "openai/gpt-4o-mini", systemPrompt: null,
            providerId: RemoteProviders.OpenRouter);
        document.Messages.Add(ChatMessage.User("hello"));
        store.Save(document);

        document.RemoteModel = "openai/gpt-4.1";
        store.Save(document);

        ChatSessionDocument reopened = store.Open(document.Id);
        Assert.Equal("openai/gpt-4.1", reopened.RemoteModel);
        Assert.Equal(RemoteProviders.OpenRouter, reopened.RemoteProvider);
        Assert.Single(reopened.Messages);
    }
}
