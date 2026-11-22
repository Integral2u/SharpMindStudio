using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// Builds the transport and protocol client from one saved server entry. All
/// secret expansion happens here, once per connection, before anything starts.
/// </summary>
public static class McpClientFactory
{
    public static McpServerClient Create(
        McpServerSettings settings,
        ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        secrets ??= NullSecretResolver.Instance;

        if (McpTransports.IsHttp(settings.Transport))
            return CreateHttp(settings, secrets);

        return CreateStdio(settings, secrets);
    }

    private static McpServerClient CreateStdio(
        McpServerSettings settings,
        ISecretResolver secrets)
    {
        var used = new List<SecretUse>();
        string command = ExpandRequired(settings.Command, secrets, used);
        var args = settings.Args
            .Select(argument => ExpandRequired(argument, secrets, used))
            .ToList();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in settings.Environment)
            environment[key] = ExpandRequired(value, secrets, used);

        var transport = new StdioMcpTransport(command, args, environment);
        return new McpServerClient(transport, secrets, used, "2024-11-05");
    }

    private static McpServerClient CreateHttp(
        McpServerSettings settings,
        ISecretResolver secrets)
    {
        string url = (settings.Url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"MCP HTTP server '{settings.Name}' needs an absolute http or https URL.");
        }

        var used = new List<SecretUse>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in settings.Headers)
            headers[name] = ExpandRequired(value, secrets, used);

        var transport = new HttpMcpTransport(url, headers);
        return new McpServerClient(transport, secrets, used, "2025-03-26");
    }

    private static string ExpandRequired(
        string text,
        ISecretResolver secrets,
        List<SecretUse> used)
    {
        SecretExpansion expansion = SecretTemplate.Expand(text, secrets.Resolve);
        if (expansion.Missing.Count > 0) throw new SecretMissingException(expansion.Missing);
        used.AddRange(expansion.Used);
        return expansion.Text;
    }
}
