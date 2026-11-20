using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterAttachmentTests
{
    [Fact]
    public void UserAttachments_BecomeContentParts()
    {
        var message = ChatMessage.User("look", "You");
        message.Artifacts =
        [
            new ChatArtifact
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "shot.png",
                Content = [1, 2, 3],
            },
            new ChatArtifact
            {
                Type = "file",
                MimeType = "application/pdf",
                FileName = "spec.pdf",
                Content = [4, 5],
            },
            new ChatArtifact
            {
                Type = "text",
                MimeType = "text/plain",
                FileName = "notes.txt",
                Content = Encoding.UTF8.GetBytes("hello"),
            },
        ];

        List<JsonObject> messages = OpenRouterMessageBuilder.BuildMessages([message], null, null);
        JsonArray parts = messages[0]["content"]!.AsArray();

        Assert.Equal(4, parts.Count);
        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,",
            parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("file", parts[2]!["type"]!.GetValue<string>());
        Assert.Equal("spec.pdf", parts[2]!["file"]!["filename"]!.GetValue<string>());
        Assert.Equal("hello", parts[3]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void NoAttachments_StaysAPlainString()
    {
        List<JsonObject> messages = OpenRouterMessageBuilder.BuildMessages(
            [ChatMessage.User("hello", "You")], null, null);

        Assert.Equal("hello", messages[0]["content"]!.GetValue<string>());
    }
}
