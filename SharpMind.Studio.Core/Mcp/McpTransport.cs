using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// Moves JSON-RPC messages. A transport knows how to start, send and receive,
/// but nothing about tools, permissions or secrets; that keeps a second
/// transport (HTTP) from growing a second permission path.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>A safe label for status. Never includes command arguments or headers.</summary>
    string Description { get; }

    /// <summary>Raw server stderr or HTTP error text. The client redacts it before showing.</summary>
    string? ErrorTail { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task SendAsync(JsonNode message, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JsonNode> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets an HTTP transport put the negotiated version on later requests.</summary>
    void SetProtocolVersion(string version)
    {
    }
}

/// <summary>
/// A newline-delimited JSON-RPC transport over supplied streams. Used by the
/// stream tests and by callers that already own the process.
/// </summary>
public sealed class StreamMcpTransport(TextWriter stdin, TextReader stdout) : IMcpTransport
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string Description => "stream";

    public string? ErrorTail => null;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task SendAsync(JsonNode message, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<JsonNode> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch
            {
                continue;
            }

            if (message is not null) yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
