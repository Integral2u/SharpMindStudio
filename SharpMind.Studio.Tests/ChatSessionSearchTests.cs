using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ChatSessionSearchTests
{
    [Fact]
    public void Search_FindsMessageTextAcrossSessions()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);

        ChatSessionDocument first = store.Create("models/a.gguf", null, null, null, null);
        first.Messages.Add(ChatMessage.User("How do I tune the sampler?"));
        first.Messages.Add(ChatMessage.Agent("Lower the temperature and raise top-p."));
        store.Save(first);

        ChatSessionDocument second = store.Create("models/b.gguf", null, null, null, null);
        second.Messages.Add(ChatMessage.User("Show me the layer lab."));
        store.Save(second);

        IReadOnlyList<ChatSessionSummary> hits = store.Search("top-p");

        ChatSessionSummary hit = Assert.Single(hits);
        Assert.Equal(first.Id, hit.Id);
        Assert.Contains("top-p", hit.Preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_MatchesTheSessionName()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);

        ChatSessionDocument document = store.Create("models/a.gguf", null, null, null, null);
        document.Name = "Audio experiments";
        store.Save(document);

        ChatSessionSummary hit = Assert.Single(store.Search("audio"));
        Assert.Equal(document.Id, hit.Id);
    }

    [Fact]
    public void Search_ReturnsNoHitsForAnUnknownTerm()
    {
        using var temp = new TempDirectory();
        var store = new ChatSessionStore(temp.Path);

        ChatSessionDocument document = store.Create("models/a.gguf", null, null, null, null);
        document.Messages.Add(ChatMessage.User("Hello"));
        store.Save(document);

        Assert.Empty(store.Search("quantization"));
    }
}
