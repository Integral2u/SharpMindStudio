using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>One tool-call fragment from an OpenRouter streaming delta.</summary>
public sealed record OpenRouterToolCallDelta(
    int Index,
    string? Id = null,
    string? Name = null,
    string? Arguments = null);

/// <summary>One parsed SSE frame from the OpenRouter chat-completions stream.</summary>
public sealed record OpenRouterStreamEvent
{
    public string? Delta { get; init; }
    public IReadOnlyList<OpenRouterToolCallDelta>? ToolCalls { get; init; }
    public string? FinishReason { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public decimal? Cost { get; init; }

    /// <summary>Native currency of <see cref="Cost"/>: USD for OpenRouter, EUR for EuroRouter.</summary>
    public string? CostCurrency { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
}

/// <summary>Metadata returned by a provider's <c>/key</c> endpoint.</summary>
public sealed record RemoteKeyInfo(
    string? Label,
    string? Name,
    decimal? Limit,
    decimal? Remaining,
    decimal? Usage,
    string? Currency);

/// <summary>A catalog entry returned by OpenRouter's <c>/models</c> endpoint.</summary>
public sealed record OpenRouterModel(
    string Id,
    string? Name,
    int? ContextLength,
    bool IsFree = false,
    bool SupportsTools = false)
{
    /// <summary>True when the provider accepts a <c>reasoning.effort</c> value.</summary>
    public bool SupportsReasoning { get; init; }

    /// <summary>Efforts the catalog says this model accepts, in canonical order.</summary>
    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];

    /// <summary>The provider's own default effort, when it reports one.</summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>Everything needed for one chat-completions request.</summary>
public sealed class OpenRouterRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<JsonObject> Messages { get; init; }
    public IReadOnlyList<JsonObject>? Tools { get; init; }
    public float? Temperature { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public float? RepetitionPenalty { get; init; }
    public int? MaxTokens { get; init; }
    public int? Seed { get; init; }
    public bool ParallelToolCalls { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>Reasoning effort sent as <c>reasoning.effort</c>; null uses the provider default.</summary>
    public string? ReasoningEffort { get; init; }
}

public sealed class OpenRouterException : Exception
{
    public OpenRouterException(
        string message,
        int? statusCode = null,
        int? errorCode = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    public int? StatusCode { get; }
    public int? ErrorCode { get; }

    /// <summary>Provider-suggested wait before retrying, when the response carried one.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Minimal OpenRouter Chat Completions client. It owns no UI state: it sends a
/// message list and yields streaming deltas, tool-call fragments, usage and
/// finish reasons. The caller owns the agent loop and tool execution.
/// </summary>
public sealed class OpenRouterClient
{
    public const string DefaultModel = OpenRouterSettingsDocument.DefaultModelId;

    private readonly HttpClient _http;

    public OpenRouterClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<IReadOnlyList<OpenRouterModel>> ListModelsAsync(
        OpenRouterConnection connection,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(connection, "models"));
        ApplyHeaders(request, connection);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var models = new List<OpenRouterModel>();
        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                    continue;
                string? name = item.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
                int? context = ReadContextLength(item);
                string modelId = id.GetString()!;
                bool isFree = modelId.EndsWith(":free", StringComparison.OrdinalIgnoreCase)
                    || IsZeroPricing(item)
                    || HasZeroCost(item);
                bool supportsTools = SupportsTools(item);
                (bool supportsReasoning, string? defaultEffort, IReadOnlyList<string> efforts) =
                    ReadReasoning(item);
                models.Add(new OpenRouterModel(modelId, name, context, isFree, supportsTools)
                {
                    SupportsReasoning = supportsReasoning,
                    DefaultReasoningEffort = defaultEffort,
                    ReasoningEfforts = efforts,
                });
            }
        }

        return [.. models
            .OrderByDescending(model => model.IsFree)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<RemoteKeyInfo> GetKeyInfoAsync(
        OpenRouterConnection connection,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(connection, "key"));
        ApplyHeaders(request, connection);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement nested)
            ? nested
            : document.RootElement;

        return new RemoteKeyInfo(
            ReadString(data, "label"),
            ReadString(data, "name"),
            ReadDecimal(data, "limit"),
            ReadDecimal(data, "limit_remaining"),
            ReadDecimal(data, "usage"),
            ReadString(data, "currency"));
    }

    public async IAsyncEnumerable<OpenRouterStreamEvent> StreamAsync(
        OpenRouterRequest request,
        OpenRouterConnection connection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!connection.IsUsable)
            throw new OpenRouterException(
                "The remote provider is not configured. Add an API key in Settings.");

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(connection, "chat/completions"));
        ApplyHeaders(httpRequest, connection);
        httpRequest.Content = new StringContent(
            BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        bool done = false;
        while (!done)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield return new OpenRouterStreamEvent { IsComplete = true };
                yield break;
            }

            if (line.Length == 0 || line[0] == ':') continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]")
            {
                done = true;
                yield return new OpenRouterStreamEvent { IsComplete = true };
                continue;
            }

            OpenRouterStreamEvent? parsed = ParseFrame(payload, out bool terminal);
            if (parsed is not null) yield return parsed;
            if (terminal) done = true;
        }
    }

    private static JsonObject BuildBody(OpenRouterRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray([.. request.Messages.Select(message => (JsonNode)message.DeepClone())]),
        };

        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)tool.DeepClone())]);
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = request.ParallelToolCalls;
        }

        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (request.TopP is { } topP) body["top_p"] = topP;
        if (request.TopK is int topK && topK > 0) body["top_k"] = topK;
        if (request.RepetitionPenalty is float repetition && repetition > 0)
            body["repetition_penalty"] = repetition;
        if (request.MaxTokens is int maxTokens && maxTokens > 0) body["max_tokens"] = maxTokens;
        if (request.Seed is { } seed) body["seed"] = seed;
        if (request.Stop is { Count: > 0 } stop)
            body["stop"] = new JsonArray([.. stop.Select(value => JsonValue.Create(value))]);
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject
            {
                ["effort"] = request.ReasoningEffort.Trim().ToLowerInvariant(),
            };
        }

        return body;
    }

    private static OpenRouterStreamEvent? ParseFrame(string payload, out bool terminal)
    {
        terminal = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                terminal = true;
                return new OpenRouterStreamEvent
                {
                    Error = ReadErrorMessage(error),
                    FinishReason = "error",
                    IsComplete = true,
                };
            }

            int? promptTokens = null;
            int? completionTokens = null;
            decimal? cost = null;
            string? costCurrency = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
                if (usage.TryGetProperty("cost", out JsonElement costElement)
                    && costElement.ValueKind == JsonValueKind.Number
                    && costElement.TryGetDecimal(out decimal parsedCost))
                    cost = parsedCost;
                costCurrency = ReadString(usage, "cost_currency");
            }

            string? delta = null;
            string? finishReason = null;
            List<OpenRouterToolCallDelta>? toolCalls = null;

            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out JsonElement finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                    // OpenRouter repeats the finish reason on the usage chunk just
                    // before [DONE], so keep reading rather than terminating here.
                    if (finishReason == "error") terminal = true;
                }

                if (choice.TryGetProperty("delta", out JsonElement deltaElement)
                    && deltaElement.ValueKind == JsonValueKind.Object)
                {
                    if (deltaElement.TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.String)
                        delta = content.GetString();

                    if (deltaElement.TryGetProperty("tool_calls", out JsonElement calls)
                        && calls.ValueKind == JsonValueKind.Array)
                    {
                        toolCalls = [];
                        foreach (JsonElement call in calls.EnumerateArray())
                        {
                            int index = call.TryGetProperty("index", out JsonElement indexElement)
                                && indexElement.TryGetInt32(out int parsedIndex) ? parsedIndex : 0;
                            string? id = ReadString(call, "id");
                            string? name = null;
                            string? arguments = null;
                            if (call.TryGetProperty("function", out JsonElement function)
                                && function.ValueKind == JsonValueKind.Object)
                            {
                                name = ReadString(function, "name");
                                arguments = ReadString(function, "arguments");
                            }
                            toolCalls.Add(new OpenRouterToolCallDelta(index, id, name, arguments));
                        }
                        if (toolCalls.Count == 0) toolCalls = null;
                    }
                }
            }

            if (delta is null && toolCalls is null && finishReason is null
                && promptTokens is null && completionTokens is null && cost is null)
                return null;

            return new OpenRouterStreamEvent
            {
                Delta = delta,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Cost = cost,
                CostCurrency = costCurrency,
                IsComplete = terminal,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<OpenRouterException> ToExceptionAsync(
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

        string? message = null;
        int? errorCode = null;
        int? statusCode = (int)response.StatusCode;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    message = ReadString(error, "message");
                    if (error.TryGetProperty("code", out JsonElement code)
                        && code.ValueKind == JsonValueKind.Number
                        && code.TryGetInt32(out int parsedCode))
                        errorCode = parsedCode;
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    message = error.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(message))
            message = string.IsNullOrWhiteSpace(body)
                ? $"The model provider returned HTTP {statusCode} ({response.ReasonPhrase})."
                : body.Trim();

        if (statusCode == 402 &&
            !message.Contains("balance", StringComparison.OrdinalIgnoreCase))
        {
            message = $"The provider balance is too low. {message}";
        }

        return new OpenRouterException(message, statusCode, errorCode, ReadRetryAfter(response));
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };

        if (delay is { } wait && wait < TimeSpan.Zero) return TimeSpan.Zero;
        return delay;
    }

    private static void ApplyHeaders(HttpRequestMessage request, OpenRouterConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        if (!string.IsNullOrWhiteSpace(connection.Referer))
            request.Headers.TryAddWithoutValidation("HTTP-Referer", connection.Referer);
        if (!string.IsNullOrWhiteSpace(connection.Title))
            request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", connection.Title);
        if (!string.IsNullOrWhiteSpace(connection.SessionHeader)
            && !string.IsNullOrWhiteSpace(connection.SessionId))
        {
            request.Headers.TryAddWithoutValidation(
                connection.SessionHeader, connection.SessionId);
        }
    }

    private static string Endpoint(OpenRouterConnection connection, string suffix)
    {
        string baseUrl = string.IsNullOrWhiteSpace(connection.BaseUrl)
            ? OpenRouterSettingsDocument.DefaultBaseUrl
            : connection.BaseUrl.Trim().TrimEnd('/');
        return $"{baseUrl}/{suffix}";
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "The model provider returned an error.";
        if (error.ValueKind == JsonValueKind.Object)
            return ReadString(error, "message") ?? "The model provider returned an error.";
        return "The model provider returned an error.";
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static decimal? ReadDecimal(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDecimal(out decimal parsed)
            ? parsed
            : null;

    private static int? ReadContextLength(JsonElement item)
    {
        if (item.TryGetProperty("context_length", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.Number
            && direct.TryGetInt32(out int parsed))
        {
            return parsed;
        }

        if (item.TryGetProperty("limit", out JsonElement limit)
            && limit.ValueKind == JsonValueKind.Object
            && limit.TryGetProperty("context", out JsonElement context)
            && context.ValueKind == JsonValueKind.Number
            && context.TryGetInt32(out int nested))
        {
            return nested;
        }

        return null;
    }

    private static bool IsZeroPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out JsonElement pricing)
            || pricing.ValueKind != JsonValueKind.Object)
            return false;
        return IsZero(pricing, "prompt") && IsZero(pricing, "completion");
    }

    /// <summary>
    /// OpenCode Go (and similar gateways) report per-million prices under
    /// <c>cost.text.{input,output}.price</c> instead of OpenRouter's token pricing.
    /// </summary>
    private static bool HasZeroCost(JsonElement item)
    {
        if (!item.TryGetProperty("cost", out JsonElement cost)
            || cost.ValueKind != JsonValueKind.Object
            || !cost.TryGetProperty("text", out JsonElement text)
            || text.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return IsZeroValue(ReadPrice(text, "input"))
            && IsZeroValue(ReadPrice(text, "output"));
    }

    private static JsonElement ReadPrice(JsonElement text, string name)
    {
        if (!text.TryGetProperty(name, out JsonElement entry)
            || entry.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return entry.TryGetProperty("price", out JsonElement price) ? price : default;
    }

    private static readonly string[] CanonicalEfforts =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Reads the catalog's reasoning block. OpenRouter reports
    /// <c>reasoning.supported_efforts</c> (and a default); gateways that only
    /// advertise the parameter still get the standard low/medium/high ladder.
    /// </summary>
    private static (bool Supports, string? DefaultEffort, IReadOnlyList<string> Efforts)
        ReadReasoning(JsonElement item)
    {
        bool supports = false;
        string? defaultEffort = null;
        var efforts = new List<string>();

        if (item.TryGetProperty("reasoning", out JsonElement reasoning)
            && reasoning.ValueKind == JsonValueKind.Object)
        {
            if (reasoning.TryGetProperty("supported_efforts", out JsonElement supported)
                && supported.ValueKind == JsonValueKind.Array)
            {
                supports = true;
                foreach (JsonElement effort in supported.EnumerateArray())
                {
                    if (effort.ValueKind == JsonValueKind.String)
                        AddEffort(efforts, effort.GetString());
                }
            }

            if (reasoning.TryGetProperty("supports_effort", out JsonElement supportsEffort)
                && supportsEffort.ValueKind is JsonValueKind.True)
            {
                supports = true;
            }

            if (reasoning.TryGetProperty("default_effort", out JsonElement defaultElement)
                && defaultElement.ValueKind == JsonValueKind.String)
            {
                defaultEffort = NormalizeEffort(defaultElement.GetString());
            }
        }

        if (item.TryGetProperty("supported_parameters", out JsonElement parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                if (parameter.ValueKind != JsonValueKind.String) continue;
                string? value = parameter.GetString();
                if (string.Equals(value, "reasoning", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "reasoning_effort", StringComparison.OrdinalIgnoreCase))
                {
                    supports = true;
                }
            }
        }

        if (supports && efforts.Count == 0)
            efforts.AddRange(["low", "medium", "high"]);

        return (supports, defaultEffort, SortEfforts(efforts));
    }

    private static void AddEffort(List<string> efforts, string? effort)
    {
        string? normalized = NormalizeEffort(effort);
        if (normalized is not null && !efforts.Contains(normalized, StringComparer.Ordinal))
            efforts.Add(normalized);
    }

    private static string? NormalizeEffort(string? effort) =>
        string.IsNullOrWhiteSpace(effort) ? null : effort.Trim().ToLowerInvariant();

    private static IReadOnlyList<string> SortEfforts(List<string> efforts) =>
        [.. efforts
            .OrderBy(effort =>
            {
                int index = Array.IndexOf(CanonicalEfforts, effort);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(effort => effort, StringComparer.Ordinal)];

    private static bool SupportsTools(JsonElement item)
    {
        if (item.TryGetProperty("supported_parameters", out JsonElement parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                if (parameter.ValueKind == JsonValueKind.String
                    && string.Equals(
                        parameter.GetString(), "tools", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return item.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("tools", out JsonElement tools)
            && tools.ValueKind == JsonValueKind.Object
            && tools.TryGetProperty("supported", out JsonElement supported)
            && supported.ValueKind == JsonValueKind.True;
    }

    private static bool IsZero(JsonElement pricing, string name) =>
        pricing.TryGetProperty(name, out JsonElement value) && IsZeroValue(value);

    private static bool IsZeroValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDecimal(out decimal number) && number == 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal parsed) && parsed == 0;
        }
        return false;
    }
}
