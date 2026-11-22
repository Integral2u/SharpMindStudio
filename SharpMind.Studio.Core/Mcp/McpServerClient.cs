using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Mcp;

public sealed record McpToolInfo(string Name, string Description, JsonNode? InputSchema);

public sealed record McpToolResult(bool IsError, string Text);

/// <summary>
/// The MCP protocol above a transport: handshake, tool discovery, calls,
/// cancellation and the one place <c>%vault:NAME%</c> is expanded and values
/// are removed from output. The transport only moves messages.
/// </summary>
public sealed class McpServerClient : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly McpJsonRpcSession _session;
    private readonly ISecretResolver _secrets;
    private readonly Lock _secretGate = new();
    private readonly HashSet<string> _secretValues = new(StringComparer.Ordinal);

    private string _protocolVersion;

    public McpServerClient(
        IMcpTransport transport,
        ISecretResolver secrets,
        IReadOnlyList<SecretUse> startupUses,
        string protocolVersion)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _secrets = secrets ?? NullSecretResolver.Instance;
        _session = new McpJsonRpcSession(transport);
        _protocolVersion = protocolVersion;
        AddSecrets(startupUses);
    }

    public string? ErrorTail
    {
        get
        {
            string? text = _transport.ErrorTail;
            return string.IsNullOrWhiteSpace(text)
                ? text
                : SecretTemplate.Redact(text, SecretValues());
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _session.StartAsync(cancellationToken).ConfigureAwait(false);

            JsonNode result = await _session
                .RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["protocolVersion"] = _protocolVersion,
                        ["capabilities"] = new JsonObject(),
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "SharpMind Studio",
                            ["version"] = "1.1.3",
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            string? negotiated = result["protocolVersion"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(negotiated))
            {
                _protocolVersion = negotiated;
                _transport.SetProtocolVersion(negotiated);
            }

            await _session
                .NotifyAsync("notifications/initialized", null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            JsonNode result = await _session
                .RequestAsync("tools/list", null, cancellationToken)
                .ConfigureAwait(false);

            if (result["tools"] is not JsonArray tools) return [];

            var list = new List<McpToolInfo>();
            foreach (JsonNode? node in tools)
            {
                if (node is not JsonObject tool) continue;
                string name = tool["name"]?.GetValue<string>() ?? "";
                if (name.Length == 0) continue;
                string description = tool["description"]?.GetValue<string>() ?? "";
                list.Add(new McpToolInfo(
                    name,
                    SecretTemplate.Redact(description, SecretValues()),
                    tool["inputSchema"]?.DeepClone()));
            }

            return list;
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    public async Task<McpToolResult> CallToolAsync(
        string tool,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        JsonNode? expandedArguments = null;
        if (arguments is not null)
        {
            expandedArguments = SecretTemplate.ExpandJson(
                arguments,
                _secrets.Resolve,
                out IReadOnlyList<SecretUse> used,
                out IReadOnlyList<string> missing);

            if (missing.Count > 0) throw new SecretMissingException(missing);
            AddSecrets(used);
        }

        try
        {
            JsonNode result = await _session
                .RequestAsync(
                    "tools/call",
                    new JsonObject
                    {
                        ["name"] = tool,
                        ["arguments"] = expandedArguments ?? new JsonObject(),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            bool isError = result["isError"]?.GetValue<bool>() ?? false;
            IReadOnlyList<string> secrets = SecretValues();

            if (result["content"] is JsonArray content)
            {
                var text = new StringBuilder();
                foreach (JsonNode? part in content)
                {
                    if (part is not JsonObject item) continue;
                    string type = item["type"]?.GetValue<string>() ?? "";
                    if (type == "text")
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(item["text"]?.GetValue<string>() ?? "");
                    }
                    else
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append($"[{type} content]");
                    }
                }
                return new McpToolResult(
                    isError,
                    SecretTemplate.Redact(text.ToString(), secrets));
            }

            return new McpToolResult(
                isError,
                SecretTemplate.Redact(result.ToJsonString(), secrets));
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    private Exception Redact(Exception ex)
    {
        if (ex is SecretMissingException) return ex;

        string message = ex.Message;
        if (ex is IOException)
        {
            string tail = ErrorTail ?? "";
            if (tail.Length > 0) message = $"{message} {tail}".Trim();
        }

        message = SecretTemplate.Redact(message, SecretValues());

        if (ex is IOException) return new IOException(message, ex);
        if (ex is TimeoutException) return new TimeoutException(message, ex);
        if (ex is OperationCanceledException) return new OperationCanceledException(message, ex);
        return new InvalidOperationException(message, ex);
    }

    private void AddSecrets(IEnumerable<SecretUse> uses)
    {
        lock (_secretGate)
        {
            foreach (SecretUse use in uses)
            {
                if (!string.IsNullOrEmpty(use.Value)) _secretValues.Add(use.Value);
            }
        }
    }

    private IReadOnlyList<string> SecretValues()
    {
        lock (_secretGate) return [.. _secretValues];
    }

    public async ValueTask DisposeAsync()
    {
        lock (_secretGate) _secretValues.Clear();
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
