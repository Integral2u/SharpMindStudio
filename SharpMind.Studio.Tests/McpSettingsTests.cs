using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpSettingsTests
{
    [Fact]
    public void Store_RoundTripsServersAndDefaultsTheName()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-mcp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new McpSettingsStore(root);
            store.Save(new McpServerSettings
            {
                Id = "files",
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-filesystem", "C:/work"],
                Environment = { ["TOKEN"] = "x" },
            });

            var reopened = new McpSettingsStore(root);
            McpServerSettings server = Assert.Single(reopened.Servers);
            Assert.Equal("files", server.Name);
            Assert.Equal("npx", server.Command);
            Assert.Equal(3, server.Args.Count);
            Assert.Equal("x", server.Environment["TOKEN"]);

            reopened.Remove("files");
            Assert.Empty(new McpSettingsStore(root).Servers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_RejectsAServerWithoutACommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-mcp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new McpSettingsStore(root);
            Assert.Throws<ArgumentException>(() =>
                store.Save(new McpServerSettings { Id = "broken" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_InitialisesAndListsTools()
    {
        var output = new StringWriter();
        var reader = new ScriptedReader(
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file","description":"Reads a file","inputSchema":{"type":"object"}}]}}""");

        await using var client = McpStdioClient.FromStreams(output, reader);

        await client.InitializeAsync();
        IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync();

        McpToolInfo tool = Assert.Single(tools);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("Reads a file", tool.Description);
        Assert.Contains("notifications/initialized", output.ToString());
    }

    [Fact]
    public async Task Client_JoinsTextContentFromAToolCall()
    {
        var reader = new ScriptedReader(
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]}}""");

        await using var client = McpStdioClient.FromStreams(new StringWriter(), reader);
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync("read_file", new JsonObject());

        Assert.False(result.IsError);
        Assert.Equal("line one\nline two", result.Text);
    }

    private sealed class ScriptedReader(params string[] lines) : TextReader
    {
        private readonly Queue<string> _lines = new(lines);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);

        public override Task<string?> ReadLineAsync() =>
            Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
    }
}
