using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// End-to-end MCP tests against a real child process. The fake server is a
/// small console project that speaks newline-delimited JSON-RPC, so these cover
/// the framing, handshake, error mapping, stderr drain and process death that
/// the stream-based tests cannot.
/// </summary>
public sealed class McpIntegrationTests
{
    private const string ServerId = "fake";

    [Fact]
    public async Task Client_HandshakesListsAndCallsATool()
    {
        await using McpStdioClient client = McpStdioClient.Start(Server());

        await client.InitializeAsync();
        IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync();

        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "echo");
        McpToolInfo echo = Assert.Single(tools, tool => tool.Name == "echo");
        Assert.Equal("Echoes the text argument", echo.Description);
        Assert.NotNull(echo.InputSchema);

        McpToolResult result = await client.CallToolAsync(
            "echo",
            new JsonObject { ["text"] = "hello" });

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task Client_MapsAnIsErrorResultWithoutThrowing()
    {
        await using McpStdioClient client = McpStdioClient.Start(Server());
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync("fail", null);

        Assert.True(result.IsError);
        Assert.Equal("tool failed", result.Text);
    }

    [Fact]
    public async Task Client_ThrowsTheServersErrorMessage()
    {
        await using McpStdioClient client = McpStdioClient.Start(Server());
        await client.InitializeAsync();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("error", null));

        Assert.Contains("deliberate failure", error.Message);
    }

    [Fact]
    public async Task Client_ReportsClosedOutputWhenTheServerDies()
    {
        await using McpStdioClient client = McpStdioClient.Start(Server());
        await client.InitializeAsync();

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => client.CallToolAsync("crash", null));

        Assert.Contains("closed its output", error.Message);
        Assert.Contains("fake server crashed", error.Message);
    }

    [Fact]
    public async Task Host_RefusesToStartWithoutConsent()
    {
        await using var host = new McpServerHost();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.EnsureStartedAsync(Server(), () => Task.FromResult(false)));

        Assert.False(host.IsRunning(ServerId));
        Assert.Empty(host.Connected);
    }

    private static McpServerSettings Server(int idleSeconds = 300) =>
        FakeMcpServer.Settings(idleSeconds);
}
