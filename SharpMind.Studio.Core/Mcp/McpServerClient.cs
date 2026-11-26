using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Mcp;

public sealed record McpToolInfo(string Name, string Description, JsonNode? InputSchema)
{
    /// <summary>The tool's <c>_meta</c> object, including A2UI UI hints.</summary>
    public JsonNode? Meta { get; init; }
}

public sealed record McpContentPart(
    string Type,
    string? Text = null,
    string? MimeType = null,
    string? Uri = null,
    byte[]? Data = null,
    JsonNode? Annotations = null,
    JsonNode? Raw = null);

public sealed record McpResourceInfo(
    string Uri,
    string Name,
    string? Description,
    string? MimeType,
    JsonNode? Meta);

public sealed record McpResourceContent(
    string Uri,
    string? MimeType,
    string? Text,
    byte[]? Data,
    JsonNode? Meta);

public sealed record McpToolResult(bool IsError, string Text)
{
    /// <summary>Structured content parts, including A2UI resources.</summary>
    public IReadOnlyList<McpContentPart> Content { get; init; } = [];

    /// <summary>The result's <c>_meta</c> object.</summary>
    public JsonNode? Meta { get; init; }

    /// <summary>Optional <c>structuredContent</c> returned by the server.</summary>
    public JsonNode? StructuredContent { get; init; }
}

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

    /// <summary>Extra capabilities advertised in the initialize handshake.</summary>
    public JsonObject? Capabilities { get; set; }

    /// <summary>Per-call <c>_meta</c>, used by stateless HTTP A2UI servers.</summary>
    public JsonObject? CallMeta { get; set; }

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
                        ["capabilities"] = Capabilities?.DeepClone() ?? new JsonObject(),
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
                    tool["inputSchema"]?.DeepClone())
                {
                    Meta = tool["_meta"]?.DeepClone(),
                });
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
            var parameters = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = expandedArguments ?? new JsonObject(),
            };
            if (CallMeta is not null)
                parameters["_meta"] = CallMeta.DeepClone();

            JsonNode result = await _session
                .RequestAsync("tools/call", parameters, cancellationToken)
                .ConfigureAwait(false);

            bool isError = result["isError"]?.GetValue<bool>() ?? false;
            IReadOnlyList<string> secrets = SecretValues();
            JsonNode? meta = result["_meta"]?.DeepClone();
            JsonNode? structured = result["structuredContent"]?.DeepClone();

            if (result["content"] is JsonArray content)
            {
                var text = new StringBuilder();
                var parts = new List<McpContentPart>();
                foreach (JsonNode? part in content)
                {
                    if (part is not JsonObject item) continue;
                    string type = item["type"]?.GetValue<string>() ?? "";
                    if (type == "text")
                    {
                        string value = item["text"]?.GetValue<string>() ?? "";
                        if (text.Length > 0) text.Append('\n');
                        text.Append(value);
                        parts.Add(new McpContentPart(
                            "text",
                            Text: SecretTemplate.Redact(value, secrets),
                            MimeType: item["mimeType"]?.GetValue<string>(),
                            Uri: item["uri"]?.GetValue<string>(),
                            Raw: item.DeepClone()));
                        continue;
                    }

                    if (type is "resource" or "embeddedResource")
                    {
                        JsonObject resource = item["resource"] as JsonObject ?? item;
                        string uri = resource["uri"]?.GetValue<string>() ?? "";
                        string mime = resource["mimeType"]?.GetValue<string>()
                            ?? item["mimeType"]?.GetValue<string>() ?? "";
                        string? resourceText = resource["text"]?.GetValue<string>();
                        byte[]? data = TryBase64(resource["blob"]?.GetValue<string>());
                        if (resourceText is not null)
                        {
                            if (text.Length > 0) text.Append('\n');
                            text.Append(resourceText);
                        }
                        else
                        {
                            if (text.Length > 0) text.Append('\n');
                            text.Append($"[resource {mime} {uri}]");
                        }

                        parts.Add(new McpContentPart(
                            "resource",
                            Text: resourceText is null
                                ? null
                                : SecretTemplate.Redact(resourceText, secrets),
                            MimeType: mime.Length == 0 ? null : mime,
                            Uri: uri.Length == 0 ? null : uri,
                            Data: data,
                            Annotations: (resource["annotations"] ?? item["annotations"])?.DeepClone(),
                            Raw: item.DeepClone()));
                        continue;
                    }

                    string contentType = type.Length == 0 ? "unknown" : type;
                    if (text.Length > 0) text.Append('\n');
                    text.Append($"[{contentType} content]");
                    parts.Add(new McpContentPart(
                        contentType,
                        MimeType: item["mimeType"]?.GetValue<string>(),
                        Data: TryBase64(item["data"]?.GetValue<string>()),
                        Annotations: item["annotations"]?.DeepClone(),
                        Raw: item.DeepClone()));
                }

                return new McpToolResult(
                    isError,
                    SecretTemplate.Redact(text.ToString(), secrets))
                {
                    Content = parts,
                    Meta = meta,
                    StructuredContent = structured,
                };
            }

            return new McpToolResult(
                isError,
                SecretTemplate.Redact(result.ToJsonString(), secrets))
            {
                Meta = meta,
                StructuredContent = structured,
            };
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            JsonNode result = await _session
                .RequestAsync("resources/list", null, cancellationToken)
                .ConfigureAwait(false);
            if (result["resources"] is not JsonArray resources) return [];

            IReadOnlyList<string> secrets = SecretValues();
            var list = new List<McpResourceInfo>();
            foreach (JsonNode? node in resources)
            {
                if (node is not JsonObject resource) continue;
                string uri = resource["uri"]?.GetValue<string>() ?? "";
                if (uri.Length == 0) continue;
                string name = resource["name"]?.GetValue<string>() ?? uri;
                string? description = resource["description"]?.GetValue<string>();
                list.Add(new McpResourceInfo(
                    uri,
                    name,
                    description is null ? null : SecretTemplate.Redact(description, secrets),
                    resource["mimeType"]?.GetValue<string>(),
                    resource["_meta"]?.DeepClone()));
            }
            return list;
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    public async Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        try
        {
            JsonNode result = await _session
                .RequestAsync(
                    "resources/read",
                    new JsonObject { ["uri"] = uri },
                    cancellationToken)
                .ConfigureAwait(false);

            JsonArray? contents = result["contents"] as JsonArray;
            if (contents is null) return [];

            IReadOnlyList<string> secrets = SecretValues();
            var list = new List<McpResourceContent>();
            foreach (JsonNode? node in contents)
            {
                if (node is not JsonObject content) continue;
                string itemUri = content["uri"]?.GetValue<string>() ?? uri;
                string? text = content["text"]?.GetValue<string>();
                list.Add(new McpResourceContent(
                    itemUri,
                    content["mimeType"]?.GetValue<string>(),
                    text is null ? null : SecretTemplate.Redact(text, secrets),
                    TryBase64(content["blob"]?.GetValue<string>()),
                    content["_meta"]?.DeepClone()));
            }
            return list;
        }
        catch (Exception ex)
        {
            throw Redact(ex);
        }
    }

    private static byte[]? TryBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Exception Redact(Exception ex)
    {
        if (ex is SecretMissingException or McpSignInRequiredException) return ex;

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
