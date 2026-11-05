using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Compute;

public sealed record ComputeHealth(
    string Status,
    string Model,
    bool ModelConfigured,
    bool ModelLoaded,
    string DataRoot,
    int RunningJobs);

public sealed record ComputeModelInfo(string Id, long Bytes, bool Active);

public sealed record ComputeStreamEvent
{
    public string? Delta { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public string? FinishReason { get; init; }
    public string? Error { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record ComputeUploadResult(string Id, string Name, string Path, long Bytes, bool Active);

public sealed record ComputeJob(
    string Id,
    string Status,
    double Progress,
    int? Step,
    double? Loss,
    double? ValLoss,
    bool? Success,
    string? ExportPath,
    string? AdapterPath,
    string? Error);

public sealed class ComputeException : Exception
{
    public ComputeException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}

/// <summary>
/// OpenAI-compatible client for a SharpMind Compute host. It owns no UI state:
/// callers pass a resolved <see cref="ComputeProvider"/> and receive parsed
/// health, model lists, stream deltas, uploads and training job snapshots.
/// </summary>
public sealed class ComputeClient
{
    private readonly HttpClient _http;

    public ComputeClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<ComputeHealth> GetHealthAsync(
        ComputeProvider provider,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            provider, HttpMethod.Get, "health", content: null, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        return new ComputeHealth(
            ReadString(root, "status") ?? "unknown",
            ReadString(root, "model") ?? "default",
            ReadBool(root, "modelConfigured"),
            ReadBool(root, "modelLoaded"),
            ReadString(root, "dataRoot") ?? "",
            ReadInt(root, "runningJobs") ?? 0);
    }

    public async Task<IReadOnlyList<ComputeModelInfo>> ListModelsAsync(
        ComputeProvider provider,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            provider, HttpMethod.Get, "v1/models", content: null, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var models = new List<ComputeModelInfo>();
        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                string? id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                models.Add(new ComputeModelInfo(
                    id,
                    ReadLong(item, "bytes") ?? 0,
                    ReadBool(item, "active")));
            }
        }

        return [.. models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public async IAsyncEnumerable<ComputeStreamEvent> StreamChatAsync(
        ComputeProvider provider,
        string model,
        IReadOnlyList<JsonObject> messages,
        float? temperature = null,
        float? topP = null,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!provider.IsUsable)
            throw new ComputeException("The Compute host is not configured. Add its URL and token in Settings.");

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray([.. messages.Select(message => (JsonNode)message.DeepClone())]),
        };
        if (temperature is { } temperatureValue) body["temperature"] = temperatureValue;
        if (topP is { } topPValue) body["top_p"] = topPValue;
        if (maxTokens is int limit && limit > 0) body["max_tokens"] = limit;

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Post,
            "v1/chat/completions",
            content,
            cancellationToken,
            StreamContentType).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield return new ComputeStreamEvent { IsComplete = true };
                yield break;
            }

            if (line.Length == 0 || line[0] == ':') continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]")
            {
                yield return new ComputeStreamEvent { IsComplete = true };
                yield break;
            }

            ComputeStreamEvent? parsed = ParseFrame(payload);
            if (parsed is not null) yield return parsed;
        }
    }

    public async Task<ComputeUploadResult> UploadModelAsync(
        ComputeProvider provider,
        string localPath,
        bool activate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            throw new ComputeException($"Model not found: {localPath}");

        string name = Path.GetFileName(localPath);
        string path = $"v1/models/{Uri.EscapeDataString(name)}"
            + (activate ? "?activate=true" : "");

        using var content = new FileUploadContent(localPath, progress);
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Put,
            path,
            content,
            cancellationToken,
            StreamContentType).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        return new ComputeUploadResult(
            ReadString(root, "id") ?? Path.GetFileNameWithoutExtension(name),
            ReadString(root, "name") ?? name,
            ReadString(root, "path") ?? "",
            ReadLong(root, "bytes") ?? 0,
            activate ? ReadBool(root, "active") : false);
    }

    public async Task<ComputeUploadResult> UploadFileAsync(
        ComputeProvider provider,
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            throw new ComputeException($"File not found: {localPath}");

        string escaped = string.Join(
            '/',
            remotePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        using var content = new FileUploadContent(localPath, progress);
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Put,
            $"v1/files/{escaped}",
            content,
            cancellationToken,
            StreamContentType).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        return new ComputeUploadResult(
            Path.GetFileName(localPath),
            Path.GetFileName(localPath),
            ReadString(root, "fullPath") ?? "",
            ReadLong(root, "bytes") ?? 0,
            false);
    }

    public async Task<ComputeJob> StartTrainingAsync(
        ComputeProvider provider,
        JsonObject job,
        CancellationToken cancellationToken = default)
    {
        var wrapped = new JsonObject { ["job"] = job.DeepClone() };
        using var content = new StringContent(
            wrapped.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Post,
            "v1/training/jobs",
            content,
            cancellationToken,
            JsonContentType).ConfigureAwait(false);

        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComputeJob> GetJobAsync(
        ComputeProvider provider,
        string id,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Get,
            $"v1/training/jobs/{Uri.EscapeDataString(id)}",
            content: null,
            cancellationToken).ConfigureAwait(false);

        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelJobAsync(
        ComputeProvider provider,
        string id,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Post,
            $"v1/training/jobs/{Uri.EscapeDataString(id)}/cancel",
            content: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the artifact stream; disposing it also releases the HTTP response.</summary>
    public async Task<Stream> DownloadArtifactAsync(
        ComputeProvider provider,
        string id,
        string? artifact = null,
        CancellationToken cancellationToken = default)
    {
        string path = $"v1/training/jobs/{Uri.EscapeDataString(id)}/download";
        if (!string.IsNullOrWhiteSpace(artifact))
            path += $"?artifact={Uri.EscapeDataString(artifact)}";

        HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Get,
            path,
            content: null,
            cancellationToken).ConfigureAwait(false);

        try
        {
            Stream inner = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseOwnedStream(inner, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Returns the training job log lines.</summary>
    public async Task<IReadOnlyList<string>> GetJobLogAsync(
        ComputeProvider provider,
        string id,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            provider,
            HttpMethod.Get,
            $"v1/training/jobs/{Uri.EscapeDataString(id)}/logs",
            content: null,
            cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var lines = new List<string>();
        if (document.RootElement.TryGetProperty("log", out JsonElement log)
            && log.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement line in log.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.String)
                    lines.Add(line.GetString() ?? "");
            }
        }
        return lines;
    }

    private static ComputeStreamEvent? ParseFrame(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                return new ComputeStreamEvent
                {
                    Error = ReadError(error),
                    FinishReason = "error",
                    IsComplete = true,
                };
            }

            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
            }

            string? delta = null;
            string? finish = null;
            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                finish = ReadString(choice, "finish_reason");
                if (choice.TryGetProperty("delta", out JsonElement deltaElement)
                    && deltaElement.ValueKind == JsonValueKind.Object)
                {
                    delta = ReadString(deltaElement, "content");
                }
            }

            if (delta is null && finish is null && promptTokens is null && completionTokens is null)
                return null;

            return new ComputeStreamEvent
            {
                Delta = delta,
                FinishReason = finish,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                IsComplete = string.Equals(finish, "error", StringComparison.OrdinalIgnoreCase),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ComputeProvider provider,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        string? accept = null)
    {
        if (!provider.IsUsable)
            throw new ComputeException("The Compute host is not configured. Add its URL and token in Settings.");

        using var request = new HttpRequestMessage(method, Endpoint(provider, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Token);
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        if (accept is not null) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ComputeException(
                $"Could not reach {provider.Name} at {provider.BaseUrl}: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            string message = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new ComputeException(message, status);
        }

        return response;
    }

    private static async Task<ComputeJob> ReadJobAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        return new ComputeJob(
            ReadString(root, "id") ?? "",
            ReadString(root, "status") ?? "unknown",
            ReadDouble(root, "progress") ?? 0,
            ReadInt(root, "step"),
            ReadDouble(root, "loss"),
            ReadDouble(root, "valLoss"),
            root.TryGetProperty("success", out JsonElement success)
                && success.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? success.GetBoolean()
                    : null,
            ReadString(root, "exportPath"),
            ReadString(root, "adapterPath"),
            ReadString(root, "error"));
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
                return ReadError(error);
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? $"The Compute host returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : body.Trim();
    }

    private static string Endpoint(ComputeProvider provider, string path)
    {
        string baseUrl = provider.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3].TrimEnd('/');
        return $"{baseUrl}/{path.TrimStart('/')}";
    }

    private static string ReadError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "Compute host error.";
        if (error.ValueKind == JsonValueKind.Object)
            return ReadString(error, "message") ?? "Compute host error.";
        return "Compute host error.";
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static long? ReadLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static double? ReadDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private const string StreamContentType = "text/event-stream";
    private const string JsonContentType = "application/json";

    /// <summary>Keeps the HTTP response alive for the lifetime of the artifact stream.</summary>
    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Streams a file with a known length and reports upload progress.</summary>
    private sealed class FileUploadContent : HttpContent
    {
        private readonly string _path;
        private readonly long _length;
        private readonly IProgress<double>? _progress;

        public FileUploadContent(string path, IProgress<double>? progress)
        {
            _path = path;
            _length = new FileInfo(path).Length;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context)
        {
            await using var file = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
            byte[] buffer = new byte[81_920];
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                sent += read;
                if (_length > 0) _progress?.Report((double)sent / _length);
            }
        }
    }
}
