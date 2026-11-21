using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>The fake server used by the MCP integration tests.</summary>
internal static class FakeMcpServer
{
    public const string Id = "fake";
    public const string EchoTool = "mcp__fake__echo";

    public static McpServerSettings Settings(int idleSeconds = 300) => new()
    {
        Id = Id,
        Name = "Fake MCP",
        Command = "dotnet",
        Args = [DllPath],
        IdleSeconds = idleSeconds,
    };

    public static string DllPath
    {
        get
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "FakeMcp",
                "SharpMind.Studio.Tests.FakeMcp.dll");
            Assert.True(
                File.Exists(path),
                $"The fake MCP server was not copied to '{path}'. Build the test project first.");
            return path;
        }
    }
}
