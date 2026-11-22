using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// Compatibility facade for the stdio client used by tests and older callers.
/// The protocol lives in <see cref="McpServerClient"/> and the process in
/// <see cref="StdioMcpTransport"/>; this type only keeps the old entry points.
/// </summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    private readonly McpServerClient _client;

    private McpStdioClient(McpServerClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Starts the server. <c>%vault:NAME%</c> in the command, arguments or
    /// environment is resolved here, once, so the value exists only in this
    /// process and the child. Throws when the command cannot be launched or a
    /// reference is missing.
    /// </summary>
    public static McpStdioClient Start(
        McpServerSettings settings,
        ISecretResolver? secrets = null) =>
        new(McpClientFactory.Create(settings, secrets));

    /// <summary>For tests: speak the protocol over supplied streams.</summary>
    public static McpStdioClient FromStreams(
        TextWriter stdin,
        TextReader stdout,
        ISecretResolver? secrets = null) =>
        new(new McpServerClient(
            new StreamMcpTransport(stdin, stdout),
            secrets ?? NullSecretResolver.Instance,
            [],
            "2024-11-05"));

    /// <summary>The last few lines of server stderr, with used secrets removed.</summary>
    public string? ErrorTail => _client.ErrorTail;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _client.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default) =>
        _client.ListToolsAsync(cancellationToken);

    public Task<McpToolResult> CallToolAsync(
        string tool,
        JsonNode? arguments,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(tool, arguments, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
