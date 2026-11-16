using SharpMind.Studio.Core.Chat;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ToolCallTextTests
{
    [Theory]
    [InlineData("<tool_call>{\"name\":\"delegate\",\"arguments\":{}}</tool_call>")]
    [InlineData("{\"tool\":\"delegate\",\"arguments\":{}}")]
    [InlineData("{\"name\":\"delegate\",\"description\":\"Hand off\",\"parameters\":{}}")]
    public void LooksUnparsed_DetectsToolEnvelopes(string text) =>
        Assert.True(ToolCallText.LooksUnparsed(text));

    [Theory]
    [InlineData("The story is about a puppy.")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksUnparsed_LeavesNormalTextAlone(string? text) =>
        Assert.False(ToolCallText.LooksUnparsed(text));
}
