using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;
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

        Assert.Equal(6, tools.Count);
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

    [Fact]
    public async Task Client_ExpandsASecretIntoTheServerEnvironment()
    {
        TestSecretResolver secrets = FakeMcpServer.Resolver(("TOKEN", "s3cret-value"));
        McpServerSettings settings = FakeMcpServer.Settings(
            environment: new Dictionary<string, string>
            {
                ["TOKEN"] = "%vault:TOKEN%",
            });

        await using McpStdioClient client = McpStdioClient.Start(settings, secrets);
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync(
            "env",
            new JsonObject { ["name"] = "TOKEN" });

        // The server saw the value; the client removed it from the result.
        Assert.Equal("***", result.Text);
    }

    [Fact]
    public async Task Client_ExpandsASecretIntoToolArgumentsAndRedactsTheEcho()
    {
        TestSecretResolver secrets = FakeMcpServer.Resolver(("TOKEN", "s3cret-value"));
        await using McpStdioClient client = McpStdioClient.Start(Server(), secrets);
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync(
            "echo",
            new JsonObject { ["text"] = "Bearer %vault:TOKEN%" });

        Assert.Equal("Bearer ***", result.Text);
    }

    [Fact]
    public async Task Client_ExpandsASecretIntoItsOwnArguments()
    {
        TestSecretResolver secrets = FakeMcpServer.Resolver(("TOKEN", "s3cret-value"));
        McpServerSettings settings = FakeMcpServer.Settings(
            arguments: ["--token", "%vault:TOKEN%"]);

        await using McpStdioClient client = McpStdioClient.Start(settings, secrets);
        await client.InitializeAsync();

        McpToolResult result = await client.CallToolAsync("argv", null);

        Assert.Contains("***", result.Text);
        Assert.DoesNotContain("s3cret-value", result.Text);
    }

    [Fact]
    public void Client_RefusesToStartWithAMissingReference()
    {
        McpServerSettings settings = FakeMcpServer.Settings(
            environment: new Dictionary<string, string>
            {
                ["TOKEN"] = "%vault:MISSING%",
            });

        SecretMissingException error = Assert.Throws<SecretMissingException>(
            () => McpStdioClient.Start(settings, FakeMcpServer.Resolver()));

        Assert.Contains("MISSING", error.Message);
    }

    [Fact]
    public async Task Client_RefusesAToolCallWithAMissingArgumentReference()
    {
        await using McpStdioClient client = McpStdioClient.Start(
            Server(),
            FakeMcpServer.Resolver());

        await client.InitializeAsync();

        SecretMissingException error = await Assert.ThrowsAsync<SecretMissingException>(
            () => client.CallToolAsync(
                "echo",
                new JsonObject { ["text"] = "%vault:MISSING%" }));

        Assert.Contains("MISSING", error.Message);
    }

    [Fact]
    public async Task Client_RedactsASecretFromTheServerErrorTail()
    {
        TestSecretResolver secrets = FakeMcpServer.Resolver(("TOKEN", "s3cret-value"));
        McpServerSettings settings = FakeMcpServer.Settings(
            environment: new Dictionary<string, string>
            {
                ["LEAK_VALUE"] = "%vault:TOKEN%",
            });

        await using McpStdioClient client = McpStdioClient.Start(settings, secrets);
        await client.InitializeAsync();

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => client.CallToolAsync("crash", null));

        Assert.Contains("***", error.Message);
        Assert.DoesNotContain("s3cret-value", error.Message);
    }

    [Fact]
    public async Task Client_StartsAWindowsCommandScript()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        string script = temp.Combine("fake server.cmd");
        await File.WriteAllTextAsync(
            script,
            $"@echo off\r\ndotnet \"{FakeMcpServer.DllPath}\" %*\r\n");

        var settings = new McpServerSettings
        {
            Id = "script",
            Name = "Script",
            Command = script,
        };

        await using McpStdioClient client = McpStdioClient.Start(settings);
        await client.InitializeAsync();

        IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync();
        Assert.Equal(6, tools.Count);
    }

    private static McpServerSettings Server(int idleSeconds = 300) =>
        FakeMcpServer.Settings(idleSeconds);
}
