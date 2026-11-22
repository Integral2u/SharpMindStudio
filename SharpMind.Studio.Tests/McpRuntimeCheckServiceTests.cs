using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpRuntimeCheckServiceTests
{
    private readonly McpRuntimeCheckService _runtime = new();

    [Fact]
    public async Task Http_ReportsThatNoLocalRuntimeIsNeeded()
    {
        McpRuntimeStatus status = await _runtime.CheckAsync(new McpServerSettings
        {
            Id = "remote",
            Transport = McpTransports.Http,
            Url = "https://example.test/mcp",
        });

        Assert.True(status.Found);
        Assert.Contains("no local runtime", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingCommand_IsReportedAsNotFound()
    {
        McpRuntimeStatus status = await _runtime.CheckAsync(new McpServerSettings
        {
            Id = "missing",
            Command = "sharpmind-runtime-that-does-not-exist",
        });

        Assert.False(status.Found);
        Assert.Contains("not found in PATH", status.Detail);
    }

    [Fact]
    public async Task Dotnet_IsFoundAndReportsAVersion()
    {
        if (McpCommandResolver.Resolve("dotnet") is null) return;

        McpRuntimeStatus status = await _runtime.CheckAsync(new McpServerSettings
        {
            Id = "dotnet",
            Command = "dotnet",
        });

        Assert.True(status.Found);
        Assert.NotNull(status.Path);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
    }

    [Fact]
    public async Task CommandScript_IsResolvedWithoutRunningIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        string script = temp.Combine("server.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nexit /b 0\r\n");

        McpRuntimeStatus status = await _runtime.CheckAsync(new McpServerSettings
        {
            Id = "script",
            Command = script,
        });

        Assert.True(status.Found);
        Assert.EndsWith(".cmd", status.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Null(status.Version);
    }
}
