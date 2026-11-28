using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Compute;

public sealed record AkamaiAccount(
    string? Company,
    string? Email,
    double Balance,
    double PendingCharges);

public sealed record AkamaiRegion(
    string Id,
    string Label,
    string Country,
    IReadOnlyList<string> Capabilities)
{
    public bool SupportsGpu => Capabilities.Any(capability =>
        capability.Contains("GPU", StringComparison.OrdinalIgnoreCase));

    public string Display => $"{Label} ({Id})";
}

public sealed record AkamaiType(
    string Id,
    string Label,
    string Class,
    int Vcpus,
    int MemoryMb,
    int DiskMb,
    int Gpus,
    int AcceleratedDevices,
    double HourlyCost,
    double MonthlyCost)
{
    public bool IsGpu =>
        string.Equals(Class, "gpu", StringComparison.OrdinalIgnoreCase) || Gpus > 0;
}

public sealed record AkamaiImage(
    string Id,
    string Label,
    string? Vendor,
    bool Deprecated);

public sealed record AkamaiSshKey(
    string Id,
    string Label,
    string PublicKey);

public sealed record AkamaiInstance(
    string Id,
    string Label,
    string Status,
    IReadOnlyList<string> Ipv4,
    string Region,
    string TypeId,
    DateTimeOffset? CreatedUtc)
{
    public string? PublicIpv4 => Ipv4.FirstOrDefault(ip => !IsPrivate(ip));

    public bool IsRunning =>
        string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(PublicIpv4);

    private static bool IsPrivate(string ip) =>
        ip.StartsWith("10.", StringComparison.Ordinal)
        || ip.StartsWith("192.168.", StringComparison.Ordinal)
        || ip.StartsWith("172.16.", StringComparison.Ordinal)
        || ip.StartsWith("172.17.", StringComparison.Ordinal)
        || ip.StartsWith("172.18.", StringComparison.Ordinal)
        || ip.StartsWith("172.19.", StringComparison.Ordinal)
        || ip.StartsWith("172.2", StringComparison.Ordinal)
        || ip.StartsWith("172.30.", StringComparison.Ordinal)
        || ip.StartsWith("172.31.", StringComparison.Ordinal);
}

public sealed class AkamaiCreateInstanceRequest
{
    public required string Region { get; init; }
    public required string TypeId { get; init; }
    public required string ImageId { get; init; }
    public required string Label { get; init; }
    public required string SshPublicKey { get; init; }
}

public sealed class AkamaiException : Exception
{
    public AkamaiException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}

/// <summary>
/// Small Akamai Cloud (Linode) API v4 client. It reads the account, regions,
/// types, images, SSH keys and instances; creates a cloud firewall and a
/// Linode; then polls and deletes it. It owns no UI state and is testable with a
/// stub HTTP handler.
/// </summary>
public sealed class AkamaiClient
{
    public const string DefaultApiBase = "https://api.linode.com/v4";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AkamaiClient(HttpClient http, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultApiBase : baseUrl)
            .TrimEnd('/');
    }

    public async Task<AkamaiAccount> GetAccountAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "account", null, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        JsonElement account = document.RootElement;
        return new AkamaiAccount(
            ReadString(account, "company"),
            ReadString(account, "email"),
            ReadDouble(account, "balance") ?? 0,
            ReadDouble(account, "uninvoiced_balance")
                ?? ReadDouble(account, "pending_charges")
                ?? 0);
    }

    public async Task<IReadOnlyList<AkamaiRegion>> ListRegionsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> items = await ReadAllPagesAsync(
            apiKey, "regions", cancellationToken).ConfigureAwait(false);
        var regions = new List<AkamaiRegion>();
        foreach (JsonElement item in items)
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            regions.Add(new AkamaiRegion(
                id,
                ReadString(item, "label") ?? id,
                ReadString(item, "country") ?? "",
                ReadStrings(item, "capabilities")));
        }

        return [.. regions.OrderBy(region => region.Country, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.Label, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<AkamaiType>> ListTypesAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> items = await ReadAllPagesAsync(
            apiKey, "linode/types", cancellationToken).ConfigureAwait(false);
        var types = new List<AkamaiType>();
        foreach (JsonElement item in items)
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            types.Add(new AkamaiType(
                id,
                ReadString(item, "label") ?? id,
                ReadString(item, "class") ?? "",
                ReadInt(item, "vcpus") ?? 0,
                ReadInt(item, "memory") ?? 0,
                ReadInt(item, "disk") ?? 0,
                ReadInt(item, "gpus") ?? 0,
                ReadInt(item, "accelerated_devices") ?? 0,
                ReadNestedDouble(item, "price", "hourly") ?? 0,
                ReadNestedDouble(item, "price", "monthly") ?? 0));
        }

        return [.. types.OrderByDescending(type => type.IsGpu)
            .ThenBy(type => type.HourlyCost)
            .ThenBy(type => type.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<AkamaiImage>> ListImagesAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> items = await ReadAllPagesAsync(
            apiKey, "images", cancellationToken).ConfigureAwait(false);
        var images = new List<AkamaiImage>();
        foreach (JsonElement item in items)
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            string label = ReadString(item, "label") ?? id;
            if (string.Equals(ReadString(item, "type"), "manual", StringComparison.OrdinalIgnoreCase))
                continue;
            images.Add(new AkamaiImage(
                id,
                label,
                ReadString(item, "vendor"),
                item.TryGetProperty("deprecated", out JsonElement deprecated)
                    && deprecated.ValueKind is JsonValueKind.True));
        }

        return [.. images.OrderBy(image => image.Deprecated)
            .ThenBy(image => image.Label, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<AkamaiSshKey>> ListSshKeysAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> items = await ReadAllPagesAsync(
            apiKey, "profile/sshkeys", cancellationToken).ConfigureAwait(false);
        var keys = new List<AkamaiSshKey>();
        foreach (JsonElement item in items)
        {
            string? id = ReadId(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            keys.Add(new AkamaiSshKey(
                id,
                ReadString(item, "label") ?? "SSH key",
                ReadString(item, "ssh_key") ?? ""));
        }

        return [.. keys.OrderBy(key => key.Label, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<AkamaiInstance>> ListInstancesAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> items = await ReadAllPagesAsync(
            apiKey, "linode/instances", cancellationToken).ConfigureAwait(false);
        return [.. items.Select(ParseInstance).OfType<AkamaiInstance>()];
    }

    public async Task<string> CreateFirewallAsync(
        string apiKey,
        string label,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["label"] = label,
            ["rules"] = new JsonObject
            {
                ["inbound_policy"] = "DROP",
                ["outbound_policy"] = "ACCEPT",
                ["inbound"] = new JsonArray(
                    Rule("ssh", "22"),
                    Rule("compute-api", "8080"),
                    Rule("studio", "5117")),
                ["outbound"] = new JsonArray(),
            },
        };

        using var content = JsonContent(body);
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Post, "networking/firewalls", content, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return ReadId(document.RootElement, "id")
            ?? throw new AkamaiException("Akamai did not return a firewall id.");
    }

    public async Task AssignFirewallDeviceAsync(
        string apiKey,
        string firewallId,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(instanceId, out long id))
            throw new AkamaiException("The Linode instance id is not numeric.");

        var body = new JsonObject
        {
            ["id"] = id,
            ["type"] = "linode",
        };
        using var content = JsonContent(body);
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Post,
            $"networking/firewalls/{Uri.EscapeDataString(firewallId)}/devices",
            content,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AkamaiInstance> CreateInstanceAsync(
        string apiKey,
        AkamaiCreateInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["region"] = request.Region,
            ["type"] = request.TypeId,
            ["image"] = request.ImageId,
            ["label"] = request.Label,
            ["booted"] = true,
            ["authorized_keys"] = new JsonArray(request.SshPublicKey),
        };
        using var content = JsonContent(body);
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Post, "linode/instances", content, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return ParseInstance(document.RootElement)
            ?? throw new AkamaiException("Akamai did not return the new instance.");
    }

    public async Task<AkamaiInstance> GetInstanceAsync(
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Get,
            $"linode/instances/{Uri.EscapeDataString(instanceId)}",
            null,
            cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return ParseInstance(document.RootElement)
            ?? throw new AkamaiException("Akamai did not return the instance.");
    }

    public async Task DeleteInstanceAsync(
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Delete,
            $"linode/instances/{Uri.EscapeDataString(instanceId)}",
            null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFirewallAsync(
        string apiKey,
        string firewallId,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Delete,
            $"networking/firewalls/{Uri.EscapeDataString(firewallId)}",
            null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Rule(string label, string port) => new()
    {
        ["label"] = label,
        ["action"] = "ACCEPT",
        ["protocol"] = "TCP",
        ["ports"] = port,
        ["addresses"] = new JsonObject
        {
            ["ipv4"] = new JsonArray("0.0.0.0/0"),
            ["ipv6"] = new JsonArray("::/0"),
        },
    };

    private async Task<IReadOnlyList<JsonElement>> ReadAllPagesAsync(
        string apiKey,
        string path,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        const int pageSize = 500;
        for (int page = 1; page <= 100; page++)
        {
            string separator = path.Contains('?') ? "&" : "?";
            using HttpResponseMessage response = await SendAsync(
                apiKey,
                HttpMethod.Get,
                $"{path}{separator}page={page}&page_size={pageSize}",
                null,
                cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in data.EnumerateArray())
                    items.Add(item.Clone());
            }

            int pages = ReadInt(root, "pages") ?? page;
            if (page >= pages) break;
        }

        return items;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string apiKey,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AkamaiException("Add an Akamai Cloud API token first.");

        using var request = new HttpRequestMessage(
            method, $"{_baseUrl}/{path.TrimStart('/')}")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        string message = body;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out JsonElement errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("reason", out JsonElement reason)
                && reason.ValueKind == JsonValueKind.String)
            {
                message = reason.GetString() ?? body;
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(message))
            message = response.ReasonPhrase ?? "Akamai Cloud request failed.";
        throw new AkamaiException(
            $"Akamai Cloud {(int)response.StatusCode}: {message.Trim()}",
            (int)response.StatusCode);
    }

    private static AkamaiInstance? ParseInstance(JsonElement item)
    {
        string? id = ReadId(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        DateTimeOffset? created = null;
        if (ReadString(item, "created") is { } createdAt
            && DateTimeOffset.TryParse(createdAt, out DateTimeOffset parsed))
        {
            created = parsed;
        }

        return new AkamaiInstance(
            id,
            ReadString(item, "label") ?? id,
            ReadString(item, "status") ?? "unknown",
            ReadStrings(item, "ipv4"),
            ReadString(item, "region") ?? "",
            ReadString(item, "type") ?? "",
            created);
    }

    private static HttpContent JsonContent(JsonObject body) =>
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out int text))
        {
            return text;
        }
        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double text))
        {
            return text;
        }
        return null;
    }

    private static double? ReadNestedDouble(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out JsonElement nested)
        && nested.ValueKind == JsonValueKind.Object
            ? ReadDouble(nested, name)
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => item.Length > 0)];
    }
}

