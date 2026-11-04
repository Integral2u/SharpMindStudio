using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterMessageBuilderTests
{
    [Fact]
    public void BuildTools_ConvertsAgentDefinitionsToOpenAiTools()
    {
        IAgentBuilder agent = new AgentBuilder("Tester")
            .WithTool(
                "read_file",
                "Read one file",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("path"),
                },
                _ => Task.FromResult("ok"));

        IReadOnlyList<JsonObject> tools = OpenRouterMessageBuilder.BuildTools(agent);

        JsonObject tool = Assert.Single(tools);
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        JsonObject function = tool["function"]!.AsObject();
        Assert.Equal("read_file", function["name"]!.GetValue<string>());
        Assert.Equal("Read one file", function["description"]!.GetValue<string>());
        Assert.NotNull(function["parameters"]!["properties"]!["path"]);
    }

    [Fact]
    public void BuildMessages_KeepsToolCallsAndResultsInOrder()
    {
        var calls = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "call_1",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["arguments"] = """{"path":"a.txt"}""",
                },
            },
        };

        var history = new List<ChatMessage>
        {
            ChatMessage.User("read the file"),
            new()
            {
                Role = ChatRole.Agent,
                Content = "",
                Metadata = new Dictionary<string, string>
                {
                    [OpenRouterMetadata.ToolCalls] = calls.ToJsonString(),
                },
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = """{"status":"success","data":"ok"}""",
                Metadata = new Dictionary<string, string>
                {
                    [OpenRouterMetadata.ToolCallId] = "call_1",
                },
            },
            ChatMessage.Agent("done"),
        };

        List<JsonObject> messages = OpenRouterMessageBuilder.BuildMessages(
            history, "be kind", agent: null);

        Assert.Equal(5, messages.Count);
        Assert.Equal("system", messages[0]["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[3]["tool_call_id"]!.GetValue<string>());
        Assert.Equal("done", messages[4]["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToToolCallsJson_OrdersFragmentsByIndex()
    {
        JsonArray calls = OpenRouterMessageBuilder.ToToolCallsJson(
        [
            new OpenRouterCompletedToolCall(1, "b", "second", "{}"),
            new OpenRouterCompletedToolCall(0, "a", "first", "{}"),
        ]);

        Assert.Equal("a", calls[0]!["id"]!.GetValue<string>());
        Assert.Equal("b", calls[1]!["id"]!.GetValue<string>());
    }
}
