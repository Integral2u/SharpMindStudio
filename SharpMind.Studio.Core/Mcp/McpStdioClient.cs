using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Mcp;

public sealed record McpToolInfo(string Name, string Description, JsonNode? InputSchema);

public sealed record McpToolResult(bool IsError, string Text);

/// <summary>
/// A stdio MCP connection: newline-delimited JSON-RPC over the server's stdin
/// and stdout, with stderr drained so a chatty server cannot deadlock. One
/// instance per running server; the caller owns the lifetime.
/// </summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly Process? _process;
    private readonly TextWriter _stdin;
    private readonly TextReader _stdout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _errors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId;

    private McpStdioClient(Process? process, TextWriter stdin, TextReader stdout)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
    }

    /// <summary>Starts the server. Throws when the command cannot be launched.</summary>
    public static McpStdioClient Start(McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
            throw new ArgumentException("An MCP server needs an id and a command.", nameof(settings));

        var startInfo = new ProcessStartInfo(settings.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in settings.Args)
            startInfo.ArgumentList.Add(argument);
        foreach ((string key, string value) in settings.Environment)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start MCP server '{settings.Name}'.");

        var client = new McpStdioClient(
            process,
            process.StandardInput,
            process.StandardOutput);

        client.DrainErrors(process.StandardError);
        return client;
    }

    /// <summary>For tests: speak the protocol over supplied streams.</summary>
    public static McpStdioClient FromStreams(TextWriter stdin, TextReader stdout) =>
        new(null, stdin, stdout);

    /// <summary>The last few lines of server stderr, for a status panel.</summary>
    public string ErrorTail
    {
        get { lock (_errors) return _errors.ToString(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RequestAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "SharpMind Studio",
                    ["version"] = "1.1.2",
                },
            },
            cancellationToken).ConfigureAwait(false);

        await NotifyAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        JsonNode result = await RequestAsync("tools/list", null, cancellationToken)
            .ConfigureAwait(false);

        if (result["tools"] is not JsonArray tools) return [];

        var list = new List<McpToolInfo>();
        foreach (JsonNode? node in tools)
        {
            if (node is not JsonObject tool) continue;
            string name = tool["name"]?.GetValue<string>() ?? "";
            if (name.Length == 0) continue;
            list.Add(new McpToolInfo(
                name,
                tool["description"]?.GetValue<string>() ?? "",
                tool["inputSchema"]?.DeepClone()));
        }

        return list;
    }

    public async Task<McpToolResult> CallToolAsync(
        string tool,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        JsonNode result = await RequestAsync(
            "tools/call",
            new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
            },
            cancellationToken).ConfigureAwait(false);

        bool isError = result["isError"]?.GetValue<bool>() ?? false;

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
            return new McpToolResult(isError, text.ToString());
        }

        return new McpToolResult(isError, result.ToJsonString());
    }

    private async Task NotifyAsync(string method, CancellationToken cancellationToken)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(notification.ToJsonString()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonNode> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int id = ++_nextId;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters is not null) request["params"] = parameters;

            await _stdin.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            while (true)
            {
                string? line = await _stdout.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException(
                        $"MCP server closed its output. {ErrorTail}".Trim());
                }
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

                if (message is not JsonObject response) continue;
                if (response["id"] is not JsonValue value ||
                    !value.TryGetValue(out int responseId) ||
                    responseId != id)
                {
                    // A notification or a response to an earlier request.
                    continue;
                }

                if (response["error"] is JsonObject error)
                {
                    throw new InvalidOperationException(
                        error["message"]?.GetValue<string>() ?? "The MCP server returned an error.");
                }

                return response["result"] ?? new JsonObject();
            }
        }
        finally
        {
            _gate.Release();
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
        _gate.Dispose();

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
        }
    }
}
