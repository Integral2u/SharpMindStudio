using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Services;

/// <summary>One place that points at a named secret.</summary>
public sealed record VaultUsage(string SecretName, string Where);

/// <summary>
/// Finds the places that reference a secret. This is deliberately a scan of the
/// saved settings, not a cached count: a hand-edited mcp.json or a provider
/// reference must show up before a delete.
/// </summary>
public sealed class VaultUsageService
{
    private readonly McpSettingsStore _mcp;

    public VaultUsageService(McpSettingsStore mcp)
    {
        _mcp = mcp;
    }

    public IReadOnlyList<VaultUsage> Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];

        var uses = new List<VaultUsage>();
        foreach (McpServerSettings server in _mcp.Servers)
        {
            if (Uses(server.Command, name))
                uses.Add(new VaultUsage(name, $"MCP {server.Name} (command)"));

            if (Uses(server.Url, name))
                uses.Add(new VaultUsage(name, $"MCP {server.Name} (URL)"));

            if (server.Args.Any(argument => Uses(argument, name)))
                uses.Add(new VaultUsage(name, $"MCP {server.Name} (arguments)"));

            foreach ((string variable, string value) in server.Environment)
            {
                if (Uses(value, name))
                    uses.Add(new VaultUsage(name, $"MCP {server.Name} (env {variable})"));
            }

            foreach ((string header, string value) in server.Headers)
            {
                if (Uses(value, name))
                    uses.Add(new VaultUsage(name, $"MCP {server.Name} (header {header})"));
            }
        }

        return uses;
    }

    private static bool Uses(string? text, string name) =>
        SecretTemplate.Names(text).Any(candidate =>
            candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
}
