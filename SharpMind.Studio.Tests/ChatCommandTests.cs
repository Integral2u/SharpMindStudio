using SharpMind.Studio.Core.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ChatCommandTests
{
    [Fact]
    public void Parse_SplitsTheCommandAndItsArgument()
    {
        ChatCommandLine? line = ChatCommandParser.Parse("/model qwen 2.5");

        Assert.NotNull(line);
        Assert.Equal("model", line.Name);
        Assert.Equal("qwen 2.5", line.Argument);
    }

    [Fact]
    public void Parse_AcceptsLeadingWhitespaceAndTrailingArgumentSpace()
    {
        ChatCommandLine? line = ChatCommandParser.Parse("   /system   you are terse   ");

        Assert.NotNull(line);
        Assert.Equal("system", line.Name);
        Assert.Equal("you are terse", line.Argument);
    }

    [Fact]
    public void Parse_ReturnsNullForOrdinaryTextAndPaths()
    {
        Assert.Null(ChatCommandParser.Parse("hello /help"));
        Assert.Null(ChatCommandParser.Parse("//runs/20260101-example"));
        Assert.Null(ChatCommandParser.Parse("/"));
        Assert.Null(ChatCommandParser.Parse(""));
    }

    [Fact]
    public void Parse_KeepsAnUnknownCommandForTheCallerToReject()
    {
        ChatCommandLine? line = ChatCommandParser.Parse("/missing value");

        Assert.NotNull(line);
        Assert.Equal("missing", line.Name);
        Assert.Equal("value", line.Argument);
        Assert.Null(ChatCommands.Find(line.Name));
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndAcceptsTheLeadingSlash()
    {
        Assert.Equal("model", ChatCommands.Find("MODEL")?.Name);
        Assert.Equal("system", ChatCommands.Find("/System")?.Name);
    }

    [Fact]
    public void Match_SearchesNamesDescriptionsAndUsage()
    {
        Assert.Contains(ChatCommands.Match("prompt"), command => command.Name == "system");
        Assert.Contains(ChatCommands.Match("MODEL"), command => command.Name == "model");
        Assert.Equal(ChatCommands.All.Count, ChatCommands.Match("").Count);
    }

    [Fact]
    public void Match_PrefersAnExactCommandNameOverADescriptionMatch()
    {
        IReadOnlyList<ChatCommandDefinition> matches = ChatCommands.Match("model");

        Assert.Equal("model", matches[0].Name);
        Assert.Contains(matches, command => command.Name == "new");
    }

    [Fact]
    public void Catalog_HasUniqueNames()
    {
        string[] names = [.. ChatCommands.All.Select(command => command.Name)];
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
