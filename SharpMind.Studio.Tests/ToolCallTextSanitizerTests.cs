using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;

namespace SharpMind.Studio.Tests;

public sealed class ToolCallTextSanitizerTests
{
    [Fact]
    public void RawQwenCall_IsRemoved_KeepingProse()
    {
        const string text =
            "I'll show the choices. " +
            """{"name":"UIShowOptionSelection","arguments":{"prompt":"Choose a color.","options":["red","green","blue"],"allowFreeText":true}}""";

        Assert.Equal("I'll show the choices.", ToolCallTextSanitizer.Sanitize(text));
    }

    [Fact]
    public void TaggedCall_IsRemoved()
    {
        const string text =
            "Checking. <tool_call>{\"tool\":\"Echo\",\"arguments\":{\"text\":\"hi\"}}</tool_call> Done.";

        Assert.Equal("Checking.  Done.", ToolCallTextSanitizer.Sanitize(text));
    }

    [Fact]
    public void TruncatedCall_IsDropped()
    {
        const string text =
            "Pick one. {\"name\":\"UIShowOptionSelection\",\"arguments\":{\"prompt\":\"Choose a color.\"";

        Assert.Equal("Pick one.", ToolCallTextSanitizer.Sanitize(text));
    }

    [Fact]
    public void LegitimateJsonAnswer_IsPreserved()
    {
        const string text = """{"answer":"red"}""";
        Assert.Equal(text, ToolCallTextSanitizer.Sanitize(text));
    }

    [Fact]
    public void Transcript_HidesToolRowsAndEmptyCalls()
    {
        var call = ChatMessage.Agent(
            """{"name":"UIShowOptionSelection","arguments":{"prompt":"Choose a color."}}""");
        var result = new ChatMessage
        {
            Role = ChatRole.Tool,
            Content = """[UIShowOptionSelection prompt=Choose a color.] green""",
        };

        IReadOnlyList<ChatMessage> visible = ChatTranscriptFilter.Visible(
        [
            ChatMessage.User("show me three colors"),
            call,
            result,
            ChatMessage.Agent("You've chosen green."),
        ]);

        Assert.Equal(2, visible.Count);
        Assert.Equal(ChatRole.User, visible[0].Role);
        Assert.Equal("You've chosen green.", visible[1].Content);
    }
}
