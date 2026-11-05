using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Compute;

public sealed record RunpodPod(
    string Id,
    string Name,
    string Status,
    string? Image,
    double? Cost,
    string? DataCenterId);

public sealed class RunpodCreateRequest
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string GpuType { get; init; }
    public int GpuCount { get; init; } = 1;
    public int DiskGb { get; init; } = 30;
    public IReadOnlyList<string> Ports { get; init; } = ["8080/http", "5117/http"];
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class RunpodException : Exception
{
    public RunpodException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}

/// <summary>
/// Small Runpod REST v2 client used by the provider panel: validate the API
/// key, create a GPU pod from the SharpMind Server image, poll it and terminate
/// it. Keeping this in Core makes it testable with a stub handler.
/// </summary>
public sealed class RunpodClient
{
    public const string DefaultApiBase = "https://api.runpod.io/v2";
    public const string DefaultGpuType = ComputeProviderPresets.DefaultGpuType;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public RunpodClient(HttpClient http, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultApiBase
            : baseUrl.TrimEnd('/');
    }

    /// <summary>Throws when the key is missing or rejected.</summary>
    public async Task ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "pods?limit=1", content: null, cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }

    public async Task<RunpodPod> CreatePodAsync(
        string apiKey,
        RunpodCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new JsonObject
        {
            ["name"] = request.Name,
            ["image"] = request.Image,
            ["gpu"] = new JsonObject
            {
                ["id"] = request.GpuType,
                ["count"] = Math.Max(1, request.GpuCount),
            },
            ["disk"] = Math.Max(1, request.DiskGb),
            ["ports"] = new JsonArray([.. request.Ports.Select(port => JsonValue.Create(port))]),
        };

        if (request.Env.Count > 0)
        {
            var env = new JsonObject();
            foreach ((string key, string value) in request.Env)
                env[key] = value;
            body["env"] = env;
        }

        using var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Post, "pods", content, cancellationToken).ConfigureAwait(false);

        return await ReadPodAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunpodPod> GetPodAsync(
        string apiKey,
        string podId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(podId);
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Get,
            $"pods/{Uri.EscapeDataString(podId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);

        return await ReadPodAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task TerminatePodAsync(
        string apiKey,
        string podId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(podId);
        using var content = new StringContent(
            """{"action":"terminate"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Post,
            $"pods/{Uri.EscapeDataString(podId)}/action",
            content,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string apiKey,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new RunpodException("A Runpod API key is required.");

        using var request = new HttpRequestMessage(method, $"{_baseUrl}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new RunpodException($"Could not reach Runpod: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            string message = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new RunpodException(message, status);
        }

        return response;
    }

    private static async Task<RunpodPod> ReadPodAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        return new RunpodPod(
            ReadString(root, "id") ?? "",
            ReadString(root, "name") ?? "pod",
            ReadString(root, "status") ?? "UNKNOWN",
            ReadString(root, "image"),
            ReadDouble(root, "cost"),
            ReadString(root, "dataCenterId"));
    }

    private static async Task<string> ReadErrorAsync(
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
            JsonElement root = document.RootElement;
            string? detail = ReadString(root, "detail");
            if (!string.IsNullOrWhiteSpace(detail)) return detail;
            string? title = ReadString(root, "title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (root.TryGetProperty("errors", out JsonElement errors)
                    && errors.ValueKind == JsonValueKind.Array)
                {
                    string[] items = [.. errors.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .Where(item => item.Length > 0)];
                    if (items.Length > 0)
                        return $"{title}: {string.Join("; ", items)}";
                }
                return title;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? $"Runpod returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : body.Trim();
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double parsed)
            ? parsed
            : null;
}
