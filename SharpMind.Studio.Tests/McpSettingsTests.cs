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
    public void Store_RoundTripsAnHttpServer()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-mcp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new McpSettingsStore(root);
            store.Save(new McpServerSettings
            {
                Id = "remote",
                Name = "Remote",
                Transport = McpTransports.Http,
                Url = "https://example.test/mcp",
                Headers =
                {
                    ["Authorization"] = "Bearer %vault:MCP_TOKEN%",
                },
            });

            var reopened = new McpSettingsStore(root);
            McpServerSettings server = Assert.Single(reopened.Servers);
            Assert.True(server.IsHttp);
            Assert.Equal("https://example.test/mcp", server.Url);
            Assert.Equal(
                "Bearer %vault:MCP_TOKEN%",
                server.Headers["Authorization"]);
            Assert.Equal("", server.Command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_RejectsAnHttpServerWithoutAUrl()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-mcp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new McpSettingsStore(root);
            Assert.Throws<ArgumentException>(() =>
                store.Save(new McpServerSettings
                {
                    Id = "remote",
                    Transport = McpTransports.Http,
                }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_DefaultsAnOlderFileToStdio()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-mcp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "mcp.json"),
                """
                {
                  "schema": "studio-mcp/1",
                  "servers": [
                    { "id": "files", "command": "npx" }
                  ]
                }
                """);

            var store = new McpSettingsStore(root);
            McpServerSettings server = Assert.Single(store.Servers);
            Assert.Equal(McpTransports.Stdio, server.Transport);
            Assert.Equal("npx", server.Command);
            Assert.Empty(server.Headers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_InitialisesAndListsTools()
    {
        var output = new SignalingWriter();
        var reader = new ScriptedReader(
            output,
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
        var output = new SignalingWriter();
        var reader = new ScriptedReader(
            output,
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]}}""");

        await using var client = McpStdioClient.FromStreams(output, reader);
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync("read_file", new JsonObject());

        Assert.False(result.IsError);
        Assert.Equal("line one\nline two", result.Text);
    }

    /// <summary>
    /// A server only answers a request with an id; notifications do not produce
    /// a line. The reader waits for one request before yielding its next reply,
    /// which is what a real stdio server does.
    /// </summary>
    private sealed class ScriptedReader(SignalingWriter writer, params string[] lines) : TextReader
    {
        private readonly Queue<string> _lines = new(lines);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await writer.WaitForRequestAsync(cancellationToken);
            return _lines.Count > 0 ? _lines.Dequeue() : null;
        }
    }

    private sealed class SignalingWriter : StringWriter
    {
        private readonly SemaphoreSlim _requests = new(0);

        public Task WaitForRequestAsync(CancellationToken cancellationToken) =>
            _requests.WaitAsync(cancellationToken);

        public override async Task WriteLineAsync(string? value)
        {
            await base.WriteLineAsync(value);
            if (value?.Contains("\"id\":", StringComparison.Ordinal) == true)
                _requests.Release();
        }
    }
}
