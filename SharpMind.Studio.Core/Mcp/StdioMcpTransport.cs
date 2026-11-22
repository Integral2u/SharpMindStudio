using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// A local MCP server: newline-delimited JSON-RPC over the process's stdin and
/// stdout, with stderr drained so a chatty server cannot deadlock.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _errors = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    private TextWriter? _stdin;
    private TextReader? _stdout;

    public StdioMcpTransport(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _command = command;
        _args = args;
        _environment = environment;
    }

    public string Description => "stdio";

    public string? ErrorTail
    {
        get
        {
            lock (_errors) return _errors.ToString().TrimEnd();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is not null) return Task.CompletedTask;

        var startInfo = McpCommandResolver.CreateStartInfo(_command, _args, _environment);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start MCP server '{_command}'.");

        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        DrainErrors(process.StandardError);
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonNode message, CancellationToken cancellationToken = default)
    {
        TextWriter stdin = _stdin
            ?? throw new InvalidOperationException("The MCP server has not started.");

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
        TextReader stdout = _stdout
            ?? throw new InvalidOperationException("The MCP server has not started.");

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

    private void DrainErrors(StreamReader stderr)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await stderr.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                    if (line is null) return;
                    lock (_errors)
                    {
                        _errors.AppendLine(line);
                        if (_errors.Length > 4000) _errors.Remove(0, _errors.Length - 4000);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _writeGate.Dispose();

        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
