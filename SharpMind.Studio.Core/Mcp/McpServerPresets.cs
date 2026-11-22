namespace SharpMind.Studio.Core.Mcp;

/// <summary>One preset header or environment entry. Values may use %vault:NAME%.</summary>
public sealed record McpPresetPair(string Name, string Value);

/// <summary>
/// A starting point for the add form. It is data only: Studio never installs a
/// runtime, fetches a package or connects on the user's behalf.
/// </summary>
public sealed record McpServerPreset(
    string Id,
    string Name,
    string Description,
    string Transport,
    string Command,
    IReadOnlyList<string> Args,
    string Url,
    IReadOnlyList<McpPresetPair> Headers,
    IReadOnlyList<McpPresetPair> Environment,
    string? Requires,
    string? DocumentationUrl);

public static class McpServerPresets
{
    public const string Custom = "custom";
    public const string Filesystem = "filesystem";
    public const string Git = "git";
    public const string Remote = "remote";

    public static IReadOnlyList<McpServerPreset> All { get; } =
    [
        new McpServerPreset(
            Custom,
            "Custom",
            "Start with empty fields.",
            McpTransports.Stdio,
            "",
            [],
            "",
            [],
            [],
            null,
            null),

        new McpServerPreset(
            Filesystem,
            "Filesystem",
            "Read and write files under one folder through the reference filesystem server.",
            McpTransports.Stdio,
            "npx",
            ["-y", "@modelcontextprotocol/server-filesystem", @"C:\work"],
            "",
            [],
            [],
            "Node.js",
            "https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem"),

        new McpServerPreset(
            Git,
            "Git",
            "Inspect a repository through the Python git server.",
            McpTransports.Stdio,
            "uvx",
            ["mcp-server-git", "--repository", @"C:\work"],
            "",
            [],
            [],
            "uv",
            "https://github.com/modelcontextprotocol/servers/tree/main/src/git"),

        new McpServerPreset(
            Remote,
            "Remote HTTP",
            "Call a hosted MCP endpoint. Add its URL and, if it needs one, an access token.",
            McpTransports.Http,
            "",
            [],
            "",
            [new McpPresetPair("Authorization", "Bearer %vault:MCP_TOKEN%")],
            [],
            null,
            null),
    ];

    public static McpServerPreset Find(string? id) =>
        All.FirstOrDefault(preset =>
            preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
