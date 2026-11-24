using SharpMind.Studio.Core.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ChatContextParserTests
{
    [Theory]
    [InlineData("@", "")]
    [InlineData("@fi", "fi")]
    [InlineData("look at @fo", "fo")]
    [InlineData("@memory", "memory")]
    public void TryGetToken_RecognisesATrailingReference(string text, string expected)
    {
        Assert.True(ChatContextParser.TryGetToken(text, out string query));
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("name@example.com")]
    [InlineData("look at @file ")]
    [InlineData("@file done")]
    public void TryGetToken_IgnoresOrdinaryText(string text)
    {
        Assert.False(ChatContextParser.TryGetToken(text, out _));
    }
}
