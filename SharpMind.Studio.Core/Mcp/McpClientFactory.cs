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
        ISecretResolver? secrets = null,
        IMcpAccessTokenProvider? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        secrets ??= NullSecretResolver.Instance;

        if (McpTransports.IsHttp(settings.Transport))
            return CreateHttp(settings, secrets, tokens);

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
        ISecretResolver secrets,
        IMcpAccessTokenProvider? tokens)
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

        Func<CancellationToken, Task<string?>>? accessToken = null;
        if (settings.UsesOAuth)
        {
            if (tokens is null)
            {
                throw new McpSignInRequiredException(
                    $"Sign in is required for MCP server '{settings.Name}'.");
            }

            headers.Remove("Authorization");
            accessToken = cancellationToken =>
                tokens.GetAccessTokenAsync(settings, cancellationToken);
        }

        var transport = new HttpMcpTransport(url, headers, accessToken: accessToken);
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
