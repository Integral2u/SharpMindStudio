using SharpMind.Inference.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SummarizingCompactorTests
{
    [Fact]
    public async Task Compact_SummarizesOlderTurnsAndKeepsSystemsAndTheCurrentTurn()
    {
        ChatMessage firstSystem = ChatMessage.System("first system prompt");
        ChatMessage secondSystem = ChatMessage.System("second system prompt");
        ChatMessage oldUser = ChatMessage.User("old question");
        ChatMessage oldAgent = ChatMessage.Agent("old answer");
        var pinned = new ChatMessage
        {
            Role = ChatRole.User,
            Content = "pinned message",
            IsPinned = true,
        };
        ChatMessage current = ChatMessage.User("current question");
        var history = new List<ChatMessage>
        {
            firstSystem, secondSystem, oldUser, oldAgent, pinned, current,
        };

        string? prompt = null;
        var context = new CompactionContext
        {
            History = history,
            CurrentTokenCount = 80,
            MaxTokens = 100,
            SummarizeAsync = text =>
            {
                prompt = text;
                return Task.FromResult("summary text");
            },
        };
        var compactor = new SummarizingCompactor();

        Assert.True(await compactor.ShouldCompactAsync(context, CancellationToken.None));
        Assert.True(await compactor.CompactAsync(context, CancellationToken.None));

        Assert.True(oldUser.Ignore);
        Assert.True(oldAgent.Ignore);
        Assert.False(firstSystem.Ignore);
        Assert.False(secondSystem.Ignore);
        Assert.False(pinned.Ignore);
        Assert.False(current.Ignore);

        Assert.NotNull(prompt);
        Assert.DoesNotContain("system prompt", prompt);
        Assert.Contains("old question", prompt);
        Assert.Contains("old answer", prompt);
        Assert.DoesNotContain("current question", prompt);

        ChatMessage summary = Assert.Single(history.Where(message =>
            message.Content.StartsWith("Summary of earlier conversation:", StringComparison.Ordinal)));
        Assert.True(summary.IsPinned);
        Assert.Equal(ChatRole.System, summary.Role);
        Assert.Contains("summary text", summary.Content);
        Assert.True(history.IndexOf(summary) > history.IndexOf(secondSystem));
        Assert.True(history.IndexOf(summary) < history.IndexOf(current));
    }

    [Fact]
    public async Task Compact_MergesAnEarlierSummaryIntoOneRollingSummary()
    {
        ChatMessage system = ChatMessage.System("system prompt");
        ChatMessage firstOld = ChatMessage.User("first old question");
        ChatMessage current = ChatMessage.User("current question");
        var history = new List<ChatMessage> { system, firstOld, current };
        var prompts = new List<string>();
        int calls = 0;
        var context = new CompactionContext
        {
            History = history,
            CurrentTokenCount = 90,
            MaxTokens = 100,
            SummarizeAsync = text =>
            {
                prompts.Add(text);
                return Task.FromResult($"summary-{++calls}");
            },
        };
        var compactor = new SummarizingCompactor();

        Assert.True(await compactor.CompactAsync(context, CancellationToken.None));
        Assert.Single(history.Where(message =>
            message.Content.StartsWith("Summary of earlier conversation:", StringComparison.Ordinal)));

        int currentIndex = history.IndexOf(current);
        history.Insert(currentIndex, ChatMessage.User("second old question"));
        Assert.True(await compactor.CompactAsync(context, CancellationToken.None));

        ChatMessage summary = Assert.Single(history.Where(message =>
            message.Content.StartsWith("Summary of earlier conversation:", StringComparison.Ordinal)));
        Assert.Contains("summary-2", summary.Content);
        Assert.Contains("summary-1", prompts[1]);
        Assert.Contains("second old question", prompts[1]);
        Assert.True(firstOld.Ignore);
        Assert.False(current.Ignore);
        Assert.DoesNotContain(history, message => message.Content.Contains("summary-1"));
    }

    [Fact]
    public async Task ShouldCompact_OnlyAboveTheThreshold()
    {
        var context = new CompactionContext
        {
            History = [],
            CurrentTokenCount = 50,
            MaxTokens = 100,
            SummarizeAsync = _ => Task.FromResult("summary"),
        };

        Assert.False(await new SummarizingCompactor().ShouldCompactAsync(
            context, CancellationToken.None));
    }
}
