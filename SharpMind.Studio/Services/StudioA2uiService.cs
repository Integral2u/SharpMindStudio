using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.A2ui;
using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the A2UI surface store for one Studio circuit, extracts fenced A2UI
/// blocks from assistant turns, and stages surfaces returned by MCP tools.
/// </summary>
public sealed class StudioA2uiService : IDisposable
{
    private readonly ThemeService _theme;
    private readonly McpServerHost _mcp;
    private readonly List<A2uiDocument> _pending = [];
    private readonly object _pendingGate = new();
    private readonly ConcurrentDictionary<string, A2uiDocument> _templates = new(StringComparer.Ordinal);

    public StudioA2uiService(ThemeService theme, McpServerHost mcp)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        Store.Changed += () => Changed?.Invoke();
        _mcp.Stopped += OnServerStopped;
    }

    public A2uiSurfaceStore Store { get; } = new();
    public bool Enabled => _theme.A2uiEnabled;
    public event Action? Changed;

    public void Dispose() => _mcp.Stopped -= OnServerStopped;

    public void Reset()
    {
        lock (_pendingGate) _pending.Clear();
        Store.Reset();
        Changed?.Invoke();
    }

    public void Enqueue(A2uiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_pendingGate) _pending.Add(document);
    }

    public IReadOnlyList<A2uiDocument> DrainPending()
    {
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return [];
            A2uiDocument[] pending = [.. _pending];
            _pending.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Finds fenced A2UI blocks in agent messages, validates them, attaches a
    /// surface artifact and removes the raw JSON from the visible text. Can be
    /// called repeatedly: a processed message no longer contains a fence.
    /// </summary>
    public int ApplyToMessages(IReadOnlyList<ChatMessage> messages)
    {
        int applied = 0;
        foreach (ChatMessage message in messages)
        {
            if (message.Role != ChatRole.Agent || string.IsNullOrWhiteSpace(message.Content))
                continue;

            A2uiExtraction extraction = A2uiText.Extract(message.Content);
            if (extraction.Blocks.Count == 0) continue;

            A2uiTurnResult result = A2uiTurnExtractor.Extract(message.Content);
            message.Content = result.Content;
            if (result.Documents.Count > 0)
            {
                var artifacts = new List<ChatArtifact>(message.Artifacts ?? []);
                foreach (A2uiDocument document in result.Documents)
                {
                    artifacts.Add(A2uiArtifact.Create(document));
                    Store.ApplyOnce(document);
                    applied++;
                }
                message.Artifacts = [.. artifacts];
            }

            if (result.Rejected)
            {
                const string note = "_The generated interface could not be rendered._";
                message.Content = string.IsNullOrWhiteSpace(message.Content)
                    ? note
                    : message.Content + "\n\n" + note;
            }
        }

        return applied;
    }

    public void AttachPending(ChatMessage assistant)
    {
        IReadOnlyList<A2uiDocument> pending = DrainPending();
        if (pending.Count == 0) return;

        var artifacts = new List<ChatArtifact>(assistant.Artifacts ?? []);
        foreach (A2uiDocument document in pending)
        {
            artifacts.Add(A2uiArtifact.Create(document));
            Store.ApplyOnce(document);
        }
        assistant.Artifacts = [.. artifacts];
    }

    /// <summary>
    /// Accepts an MCP tool result. A2UI resource parts become surface documents;
    /// a result that only carries an updateDataModel message is combined with the
    /// cached resource template. Returns a text summary for the model, or null
    /// when the result does not contain A2UI.
    /// </summary>
    public async Task<string?> AcceptMcpResultAsync(
        string serverId,
        McpToolResult result,
        JsonNode? toolMeta = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        string? resultUri = ResourceUri(result.Meta) ?? ResourceUri(toolMeta);
        bool hasA2uiPart = result.Content.Any(part =>
            IsA2uiResource(part) || IsA2uiMime(part.MimeType) ||
            (part.Type == "text" && IsA2uiMime(part.MimeType)));
        if (!hasA2uiPart && resultUri is null) return null;

        bool added = false;
        foreach (McpContentPart part in result.Content)
        {
            if (part.Type == "resource" && !IsA2uiResource(part) && !IsA2uiMime(part.MimeType))
                continue;
            if (part.Type != "resource" && !IsA2uiMime(part.MimeType))
                continue;

            string? json = PartJson(part);
            if (string.IsNullOrWhiteSpace(json)) continue;

            if (!A2uiDocument.TryParse(json, out A2uiDocument document, out _))
                continue;

            A2uiDocument[] documents = await ExpandAsync(
                serverId, document, resultUri ?? part.Uri, cancellationToken)
                .ConfigureAwait(false);
            foreach (A2uiDocument surface in documents.SelectMany(SplitBySurface))
            {
                if (HasTemplate(surface) && !A2uiValidator.Validate(surface).Success)
                    continue;
                Enqueue(surface);
                added = true;
            }
        }

        if (!added && resultUri is not null)
        {
            A2uiDocument? template = await ReadTemplateAsync(serverId, resultUri, cancellationToken)
                .ConfigureAwait(false);
            if (template is not null)
            {
                Enqueue(Decorate(template, serverId, resultUri));
                added = true;
            }
        }

        if (!added) return null;

        string summary = string.Join(" ", result.Content
            .Where(part => part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => part.Text!.Trim()));
        if (summary.Length == 0) summary = "Rendered an A2UI interface.";
        return summary.Length > 2000 ? summary[..2000] : summary;
    }

    /// <summary>Forwards an action to the server's a2ui_action tool.</summary>
    public async Task<string> ExecuteMcpActionAsync(
        A2uiActionRequest action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.ServerId))
            throw new InvalidOperationException("The A2UI action has no MCP server.");
        if (!_mcp.IsRunning(action.ServerId))
            throw new InvalidOperationException("The MCP server is not connected.");

        string tool = string.IsNullOrWhiteSpace(action.ActionTool)
            ? "a2ui_action"
            : action.ActionTool;

        var arguments = new JsonObject
        {
            ["name"] = action.Name,
            ["surfaceId"] = action.SurfaceId,
            ["sourceComponentId"] = action.SourceComponentId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["context"] = action.Context.DeepClone(),
        };

        McpToolResult result = await _mcp
            .CallAsync(action.ServerId, tool, arguments, cancellationToken)
            .ConfigureAwait(false);
        string? summary = await AcceptMcpResultAsync(
            action.ServerId, result, toolMeta: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        string text = summary ?? result.Text;
        return text.Length > 2000 ? text[..2000] : text;
    }

    private async Task<A2uiDocument[]> ExpandAsync(
        string serverId,
        A2uiDocument document,
        string? resourceUri,
        CancellationToken cancellationToken)
    {
        bool hasTemplate = document.Messages.Any(message =>
            message.CreateSurface is not null || message.UpdateComponents is not null);
        A2uiEnvelope[] updates = [.. document.Messages
            .Where(message => message.UpdateDataModel is not null)];

        if (hasTemplate)
        {
            if (!A2uiValidator.Validate(document).Success) return [];
            if (resourceUri is not null)
                _templates[TemplateKey(serverId, resourceUri)] = Decorate(document, serverId, resourceUri);
            return [Decorate(document, serverId, resourceUri)];
        }

        if (updates.Length == 0) return [];

        A2uiDocument? template = resourceUri is null
            ? null
            : await ReadTemplateAsync(serverId, resourceUri, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return
            [
                new A2uiDocument
                {
                    Origin = "mcp",
                    ServerId = serverId,
                    ResourceUri = resourceUri,
                    ActionTool = "a2ui_action",
                    Messages = updates,
                },
            ];
        }

        return
        [
            new A2uiDocument
            {
                Origin = "mcp",
                ServerId = serverId,
                ResourceUri = resourceUri,
                ActionTool = "a2ui_action",
                Messages = [.. template.Messages, .. updates],
            },
        ];
    }

    private async Task<A2uiDocument?> ReadTemplateAsync(
        string serverId,
        string uri,
        CancellationToken cancellationToken)
    {
        string key = TemplateKey(serverId, uri);
        if (_templates.TryGetValue(key, out A2uiDocument? cached))
            return cached;

        IReadOnlyList<McpResourceContent> contents;
        try
        {
            contents = await _mcp.ReadResourceAsync(serverId, uri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        foreach (McpResourceContent content in contents)
        {
            string? json = content.Text;
            if (string.IsNullOrWhiteSpace(json) && content.Data is not null)
                json = Encoding.UTF8.GetString(content.Data);
            if (string.IsNullOrWhiteSpace(json)) continue;
            if (!A2uiDocument.TryParse(json, out A2uiDocument document, out _)) continue;
            if (!document.Messages.Any(message =>
                    message.CreateSurface is not null || message.UpdateComponents is not null))
                continue;

            A2uiDocument decorated = Decorate(document, serverId, uri);
            _templates[key] = decorated;
            return decorated;
        }

        return null;
    }

    private static A2uiDocument Decorate(        A2uiDocument document,
        string serverId,
        string? resourceUri) => new()
    {
        Schema = document.Schema,
        Origin = "mcp",
        ServerId = serverId,
        ResourceUri = resourceUri,
        ActionTool = "a2ui_action",
        Messages = document.Messages,
    };

    private static string? PartJson(McpContentPart part)
    {
        if (!string.IsNullOrWhiteSpace(part.Text)) return part.Text;
        return part.Data is { Length: > 0 } ? Encoding.UTF8.GetString(part.Data) : null;
    }

    private static bool HasTemplate(A2uiDocument document) =>
        document.Messages.Any(message =>
            message.CreateSurface is not null || message.UpdateComponents is not null);

    private static IEnumerable<A2uiDocument> SplitBySurface(A2uiDocument document)
    {
        foreach (IGrouping<string, A2uiEnvelope> group in document.Messages
                     .GroupBy(message => message.SurfaceId ?? ""))
        {
            if (group.Key.Length == 0) continue;
            yield return new A2uiDocument
            {
                Schema = document.Schema,
                Origin = document.Origin,
                ServerId = document.ServerId,
                ResourceUri = document.ResourceUri,
                ActionTool = document.ActionTool,
                Messages = [.. group],
            };
        }
    }

    private static bool IsA2uiResource(McpContentPart part) =>
        part.Type == "resource" &&
        (IsA2uiMime(part.MimeType) ||
         part.Uri?.StartsWith("a2ui://", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsA2uiMime(string? mime) =>
        !string.IsNullOrWhiteSpace(mime) &&
        (mime.StartsWith("application/a2ui+json", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("application/vnd.sharpmind.a2ui+json", StringComparison.OrdinalIgnoreCase));

    private static string? ResourceUri(JsonNode? meta) =>
        meta?["ui"]?["resourceUri"]?.GetValue<string>();

    private static string TemplateKey(string serverId, string uri) =>
        serverId + "\u001f" + uri;

    private void OnServerStopped(string serverId)
    {
        foreach (string key in _templates.Keys
                     .Where(key => key.StartsWith(serverId + "\u001f", StringComparison.Ordinal))
                     .ToList())
        {
            _templates.TryRemove(key, out _);
        }
    }
}
