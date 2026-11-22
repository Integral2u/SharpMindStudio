using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// Streamable HTTP MCP transport: one POST per JSON-RPC message, JSON or
/// server-sent events in the response, and the session id echoed on the next
/// request. The foundation reads server messages that arrive on a POST; a
/// separate GET stream for unsolicited server messages is not used yet.
/// </summary>
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Channel<JsonNode> _messages = Channel.CreateUnbounded<JsonNode>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly StringBuilder _errors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private string? _sessionId;
    private string _protocolVersion;
    private bool _started;

    public HttpMcpTransport(
        string url,
        IReadOnlyDictionary<string, string> headers,
        HttpClient? http = null,
        string protocolVersion = "2025-03-26")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        _url = url.Trim();
        _headers = headers;
        _protocolVersion = protocolVersion;

        if (http is null)
        {
            _http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public string Description => "http";

    public string? ErrorTail
    {
        get
        {
            lock (_errors) return _errors.ToString().TrimEnd();
        }
    }

    public void SetProtocolVersion(string version)
    {
        if (!string.IsNullOrWhiteSpace(version)) _protocolVersion = version.Trim();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(_url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"MCP HTTP server URL '{_url}' must be an absolute http or https URL.");
        }
        _started = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonNode message, CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("The MCP HTTP transport has not started.");

        var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(
                message.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        if (!string.IsNullOrWhiteSpace(_sessionId))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);

        foreach ((string name, string value) in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppendError(ex.Message);
            request.Dispose();
            throw;
        }

        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? ids))
            _sessionId = ids.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            string detail = FirstLine(body);
            string error =
                $"MCP HTTP {((int)response.StatusCode)} {response.ReasonPhrase}"
                + (detail.Length > 0 ? $": {detail}" : ".");
            AppendError(error);
            response.Dispose();
            request.Dispose();
            throw new InvalidOperationException(error);
        }

        // The response body is read in the background so a request can wait for
        // its JSON-RPC reply while an event stream stays open.
        _ = PumpAsync(response, request, _lifetime.Token);
    }

    public async IAsyncEnumerable<JsonNode> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (JsonNode message in _messages.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async Task PumpAsync(
        HttpResponseMessage response,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var data = new StringBuilder();

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        await FlushEventAsync(data, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string value = line[5..].TrimStart();
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                }

                await FlushEventAsync(data, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string body = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                    await EnqueueAsync(body, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppendError(ex.Message);
        }
        finally
        {
            response.Dispose();
            request.Dispose();
        }
    }

    private async Task FlushEventAsync(
        StringBuilder data,
        CancellationToken cancellationToken)
    {
        if (data.Length == 0) return;

        string json = data.ToString();
        data.Clear();
        await EnqueueAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueAsync(string json, CancellationToken cancellationToken)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            AppendError($"Could not parse MCP HTTP response: {ex.Message}");
            return;
        }

        if (message is not null)
            await _messages.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private void AppendError(string message)
    {
        lock (_errors)
        {
            _errors.AppendLine(message);
            if (_errors.Length > 4000) _errors.Remove(0, _errors.Length - 4000);
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int index = text.IndexOfAny(['\r', '\n']);
        string line = index >= 0 ? text[..index] : text;
        return line.Trim();
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _messages.Writer.TryComplete();

        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
