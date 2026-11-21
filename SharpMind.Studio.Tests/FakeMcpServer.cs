using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Tests;

/// <summary>The fake server used by the MCP integration tests.</summary>
internal static class FakeMcpServer
{
    public const string Id = "fake";
    public const string EchoTool = "mcp__fake__echo";
    public const string EnvTool = "mcp__fake__env";
    public const string ArgvTool = "mcp__fake__argv";
    public const string CrashTool = "mcp__fake__crash";

    public static McpServerSettings Settings(
        int idleSeconds = 300,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { DllPath };
        if (arguments is not null) args.AddRange(arguments);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment) env[key] = value;
        }

        return new McpServerSettings
        {
            Id = Id,
            Name = "Fake MCP",
            Command = "dotnet",
            Args = args,
            Environment = env,
            IdleSeconds = idleSeconds,
        };
    }

    public static TestSecretResolver Resolver(
        params (string Name, string Value)[] values) => new(values);

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
