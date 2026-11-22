using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// One JSON-RPC session over a transport: request ids, pending replies,
/// notifications, one timeout rule and one place that notices a closed stream.
/// </summary>
public sealed class McpJsonRpcSession : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IMcpTransport _transport;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _readGate = new();
    private Task? _readLoop;
    private bool _started;

    public McpJsonRpcSession(IMcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string? ErrorTail => _transport.ErrorTail;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;

        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    /// <summary>
    /// Starts the one reader. It is deferred until the first request so a
    /// scripted stream cannot consume a reply before its request is pending.
    /// </summary>
    private void EnsureReadLoop()
    {
        if (_readLoop is not null) return;

        lock (_readGate)
        {
            _readLoop ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
        }
    }

    public async Task<JsonNode> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("The MCP session has not started.");

        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending) _pending[id] = completion;
        EnsureReadLoop();

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) request["params"] = parameters;

        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                await _transport.SendAsync(request, timeout.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The MCP server did not answer '{method}' within {RequestTimeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    public Task NotifyAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("The MCP session has not started.");

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null) notification["params"] = parameters;

        return _transport.SendAsync(notification, cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (JsonNode message in _transport
                .ReceiveAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (message is not JsonObject response) continue;

                // A server request or notification is not a reply to us.
                if (response["method"] is not null) continue;
                if (response["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out int id))
                {
                    continue;
                }

                TaskCompletionSource<JsonNode>? completion;
                lock (_pending)
                {
                    if (!_pending.Remove(id, out completion)) continue;
                }

                if (response["error"] is JsonObject error)
                {
                    string messageText = error["message"]?.GetValue<string>()
                        ?? "The MCP server returned an error.";
                    completion.SetException(new InvalidOperationException(messageText));
                }
                else
                {
                    completion.SetResult(response["result"] ?? new JsonObject());
                }
            }

            FailPending(new IOException("The MCP server closed its output."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
    }

    private void FailPending(Exception error)
    {
        TaskCompletionSource<JsonNode>[] pending;
        lock (_pending)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonNode> completion in pending)
            completion.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        FailPending(new OperationCanceledException("The MCP session was disposed."));

        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
    }

    private int _nextId;
}
