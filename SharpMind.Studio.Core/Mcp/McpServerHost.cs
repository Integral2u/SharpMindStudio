using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>A connected server and the tools it advertised at connect time.</summary>
public sealed record McpServerTools(
    string ServerId,
    string ServerName,
    IReadOnlyList<McpToolInfo> Tools);

/// <summary>
/// Owns the running MCP server processes. A server starts only when the caller
/// asks and the caller has already obtained consent; tools are cached from the
/// connect handshake, so building an agent never spawns a process.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private sealed record Entry(
        McpServerSettings Settings,
        McpStdioClient Client,
        IReadOnlyList<McpToolInfo> Tools,
        DateTimeOffset LastUsed);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _running = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning(string serverId)
    {
        lock (_gate) return _running.ContainsKey(serverId);
    }

    public IReadOnlyList<McpServerTools> Connected
    {
        get
        {
            lock (_gate)
            {
                return [.. _running.Values.Select(entry =>
                    new McpServerTools(entry.Settings.Id, entry.Settings.Name, entry.Tools))];
            }
        }
    }

    /// <summary>
    /// Starts the server when needed and returns its tools. <paramref name="allowStart"/>
    /// is the host's consent check: the Run permission on the project root.
    /// </summary>
    public async Task<IReadOnlyList<McpToolInfo>> EnsureStartedAsync(
        McpServerSettings server,
        Func<Task<bool>> allowStart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(allowStart);

        lock (_gate)
        {
            if (_running.TryGetValue(server.Id, out Entry? existing))
                return existing.Tools;
        }

        if (!await allowStart().ConfigureAwait(false))
            throw new UnauthorizedAccessException(
                $"Running MCP server '{server.Name}' needs the Run permission for this project.");

        McpStdioClient client = McpStdioClient.Start(server);
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync(cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_running.TryGetValue(server.Id, out Entry? raced))
                {
                    // Another caller won the race; keep theirs and drop ours.
                    _ = client.DisposeAsync();
                    return raced.Tools;
                }

                _running[server.Id] = new Entry(server, client, tools, DateTimeOffset.UtcNow);
            }

            return tools;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<McpToolResult> CallAsync(
        string serverId,
        string tool,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_running.TryGetValue(serverId, out Entry? found))
                throw new InvalidOperationException($"MCP server '{serverId}' is not connected.");
            entry = found;
            _running[serverId] = entry with { LastUsed = DateTimeOffset.UtcNow };
        }

        return await entry.Client
            .CallToolAsync(tool, arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(string serverId)
    {
        Entry? entry = null;
        lock (_gate)
        {
            if (_running.Remove(serverId, out Entry? found)) entry = found;
        }

        if (entry is not null) await entry.Client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Stops every server that has been idle past its own timeout.</summary>
    public async Task StopIdleAsync()
    {
        List<Entry> stale = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach ((string id, Entry entry) in _running.ToList())
            {
                if (entry.Settings.IdleSeconds <= 0) continue;
                if ((now - entry.LastUsed).TotalSeconds < entry.Settings.IdleSeconds) continue;
                _running.Remove(id);
                stale.Add(entry);
            }
        }

        foreach (Entry entry in stale)
            await entry.Client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> entries;
        lock (_gate)
        {
            entries = [.. _running.Values];
            _running.Clear();
        }

        foreach (Entry entry in entries)
            await entry.Client.DisposeAsync().ConfigureAwait(false);
    }
}
