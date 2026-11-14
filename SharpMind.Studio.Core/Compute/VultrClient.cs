using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Compute;

public sealed record VultrAccount(
    string? Name,
    string? Email,
    double Balance,
    double PendingCharges);

public sealed record VultrPlan(
    string Id,
    string Type,
    int VcpuCount,
    int RamMb,
    int DiskGb,
    double MonthlyCost,
    double HourlyCost,
    int GpuVramGb,
    string? GpuType,
    IReadOnlyList<string> Regions)
{
    public bool IsGpu => GpuVramGb > 0 || Id.StartsWith("vcg-", StringComparison.OrdinalIgnoreCase);
}

public sealed record VultrRegion(
    string Id,
    string City,
    string Country,
    string Continent)
{
    public string Display => $"{City}, {Country} ({Id})";
}

public sealed record VultrOs(int Id, string Name, string Arch);

public sealed record VultrSshKey(string Id, string Name, string PublicKey);

public sealed record VultrInstance(
    string Id,
    string Label,
    string Status,
    string? MainIp,
    string PowerStatus,
    string ServerStatus,
    string Region,
    string Plan,
    double PendingCharges,
    DateTimeOffset? CreatedUtc,
    string? Tag = null)
{
    public bool IsRunning =>
        string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase)
        && string.Equals(PowerStatus, "running", StringComparison.OrdinalIgnoreCase);
}

public sealed class VultrCreateInstanceRequest
{
    public required string Region { get; init; }
    public required string Plan { get; init; }
    public required int OsId { get; init; }
    public required string Label { get; init; }
    public required string Hostname { get; init; }
    public IReadOnlyList<string> SshKeyIds { get; init; } = [];
    public string? FirewallGroupId { get; init; }
    public string? Tag { get; init; } = "sharpmind";
}

public sealed class VultrException : Exception
{
    public VultrException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}

/// <summary>
/// Small Vultr API v2 client. It owns no UI state: list account, plans, regions,
/// operating systems, SSH keys and instances; create a firewall and an instance;
/// poll and destroy it. Keeping it in Core makes it testable with a stub handler.
/// </summary>
public sealed class VultrClient
{
    public const string DefaultApiBase = "https://api.vultr.com/v2";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public VultrClient(HttpClient http, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultApiBase
            : baseUrl.TrimEnd('/');
    }

    public async Task<VultrAccount> GetAccountAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "account", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        JsonElement account = Envelope(document.RootElement, "account");
        return new VultrAccount(
            ReadString(account, "name"),
            ReadString(account, "email"),
            ReadDouble(account, "balance") ?? 0,
            ReadDouble(account, "pending_charges") ?? 0);
    }

    public async Task<IReadOnlyList<VultrPlan>> ListPlansAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "plans?per_page=500", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var plans = new List<VultrPlan>();
        foreach (JsonElement item in Array(document.RootElement, "plans"))
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            plans.Add(new VultrPlan(
                id,
                ReadString(item, "type") ?? "",
                ReadInt(item, "vcpu_count") ?? 0,
                ReadInt(item, "ram") ?? 0,
                ReadInt(item, "disk") ?? 0,
                ReadDouble(item, "monthly_cost") ?? 0,
                ReadDouble(item, "hourly_cost") ?? 0,
                ReadInt(item, "gpu_vram_gb") ?? 0,
                ReadString(item, "gpu_type"),
                ReadStrings(item, "locations")));
        }

        return [.. plans.OrderByDescending(plan => plan.IsGpu)
            .ThenBy(plan => plan.HourlyCost)
            .ThenBy(plan => plan.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<VultrRegion>> ListRegionsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "regions?per_page=500", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var regions = new List<VultrRegion>();
        foreach (JsonElement item in Array(document.RootElement, "regions"))
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            regions.Add(new VultrRegion(
                id,
                ReadString(item, "city") ?? id,
                ReadString(item, "country") ?? "",
                ReadString(item, "continent") ?? ""));
        }

        return [.. regions.OrderBy(region => region.Country, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.City, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<VultrOs>> ListOsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "os?per_page=500", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var systems = new List<VultrOs>();
        foreach (JsonElement item in Array(document.RootElement, "os"))
        {
            int? id = ReadInt(item, "id");
            string? name = ReadString(item, "name");
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;
            systems.Add(new VultrOs(id.Value, name, ReadString(item, "arch") ?? "x64"));
        }

        return [.. systems.OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<VultrSshKey>> ListSshKeysAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "ssh-keys?per_page=500", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var keys = new List<VultrSshKey>();
        foreach (JsonElement item in Array(document.RootElement, "ssh_keys"))
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            keys.Add(new VultrSshKey(
                id,
                ReadString(item, "name") ?? "SSH key",
                ReadString(item, "ssh_key") ?? ""));
        }

        return [.. keys.OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<string> CreateFirewallGroupAsync(
        string apiKey,
        string description,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["description"] = description };
        using var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Post, "firewalls", content, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        JsonElement group = Envelope(document.RootElement, "firewall_group");
        return ReadString(group, "id")
            ?? throw new VultrException("Vultr did not return a firewall group id.");
    }

    public async Task AddFirewallRuleAsync(
        string apiKey,
        string groupId,
        string protocol,
        string port,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["ip_type"] = "v4",
            ["protocol"] = protocol,
            ["subnet"] = "0.0.0.0",
            ["subnet_size"] = 0,
            ["port"] = port,
        };
        using var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Post,
            $"firewalls/{Uri.EscapeDataString(groupId)}/rules",
            content,
            cancellationToken).ConfigureAwait(false);
        response.Dispose();
    }

    public async Task<VultrInstance> CreateInstanceAsync(
        string apiKey,
        VultrCreateInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new JsonObject
        {
            ["region"] = request.Region,
            ["plan"] = request.Plan,
            ["os_id"] = request.OsId,
            ["label"] = request.Label,
            ["hostname"] = request.Hostname,
            ["enable_ipv6"] = false,
            ["backups"] = "disabled",
        };

        if (request.SshKeyIds.Count > 0)
            body["sshkey_id"] = new JsonArray([.. request.SshKeyIds.Select(id => JsonValue.Create(id))]);
        if (!string.IsNullOrWhiteSpace(request.FirewallGroupId))
            body["firewall_group_id"] = request.FirewallGroupId;
        if (!string.IsNullOrWhiteSpace(request.Tag))
            body["tag"] = request.Tag;

        using var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Post, "instances", content, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return ReadInstance(Envelope(document.RootElement, "instance"));
    }

    public async Task<VultrInstance> GetInstanceAsync(
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Get,
            $"instances/{Uri.EscapeDataString(instanceId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return ReadInstance(Envelope(document.RootElement, "instance"));
    }

    public async Task<IReadOnlyList<VultrInstance>> ListInstancesAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            apiKey, HttpMethod.Get, "instances?per_page=500", content: null, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var instances = new List<VultrInstance>();
        foreach (JsonElement item in Array(document.RootElement, "instances"))
            instances.Add(ReadInstance(item));
        return instances;
    }

    public async Task DeleteInstanceAsync(
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Delete,
            $"instances/{Uri.EscapeDataString(instanceId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
        response.Dispose();
    }

    public async Task DeleteFirewallGroupAsync(
        string apiKey,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        using HttpResponseMessage response = await SendAsync(
            apiKey,
            HttpMethod.Delete,
            $"firewalls/{Uri.EscapeDataString(groupId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
        response.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        string apiKey,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new VultrException("A Vultr API key is required.");

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
            throw new VultrException($"Could not reach Vultr: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            string message = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new VultrException(message, status);
        }

        return response;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
            string? error = ReadString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (root.TryGetProperty("status", out JsonElement status)
                    && status.ValueKind == JsonValueKind.Number
                    && status.TryGetInt32(out int code))
                {
                    return $"{error} (HTTP {code})";
                }
                return error;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? $"Vultr returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : body.Trim();
    }

    private static VultrInstance ReadInstance(JsonElement item) =>
        new(
            ReadString(item, "id") ?? "",
            ReadString(item, "label") ?? "",
            ReadString(item, "status") ?? "unknown",
            ReadString(item, "main_ip"),
            ReadString(item, "power_status") ?? "",
            ReadString(item, "server_status") ?? "",
            ReadString(item, "region") ?? "",
            ReadString(item, "plan") ?? "",
            ReadDouble(item, "pending_charges") ?? 0,
            ReadDate(item, "date_created"),
            ReadString(item, "tag"));

    private static JsonElement Envelope(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out JsonElement value)
            ? value
            : root;

    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

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

    private static double? ReadDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
        => ReadString(element, name) is { Length: > 0 } text
           && DateTimeOffset.TryParse(text, out DateTimeOffset parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => item.Length > 0)];
    }
}
