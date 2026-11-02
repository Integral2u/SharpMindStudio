using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using QRCoder;
using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record PairingOffer(PairingSession Session, string Token, string Pin);
public sealed record LanAddress(string Address, string Url);
public sealed record UpstreamHealth(bool Healthy, int? Status, long LatencyMs, string Detail, string? Version);

public sealed class GatewayService : IDisposable
{
    public const string DeviceCookie = "sharpmind_device";

    private readonly GatewayStore _store;
    private readonly StudioOptions _options;
    private readonly ConcurrentDictionary<string, string> _approvedTokens = new();
    private readonly List<Channel<PairingSession>> _subscribers = [];
    private readonly Lock _subscriberGate = new();
    private readonly string _keyFile;
    private readonly HttpClient _healthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4),
    };
    private readonly HttpClient _publicIpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    public GatewayService(IOptions<StudioOptions> options)
    {
        _options = options.Value;
        StudioPaths.EnsureCreated();
        _store = new GatewayStore(StudioPaths.Gateway);
        _keyFile = Path.Combine(_store.Root, "gateway.key");
    }

    /// <summary>Whether phones on the LAN listener are served. Read live, so the toggle needs no restart.</summary>
    public bool LanAllowed => _options.LanEnabled || Config.LanEnabled;

    public int LanPort =>
        Uri.TryCreate(_options.LanUrl, UriKind.Absolute, out Uri? lanUri) ? lanUri.Port : 5118;

    public string DataFolder => _store.Root;
    public GatewayConfig Config => _store.LoadConfig();

    public void SaveLanEnabled(bool enabled)
    {
        GatewayConfig config = _store.LoadConfig();
        _store.SaveConfig(config with { LanEnabled = enabled });
    }

    public string? ExternalUrl => Config.ExternalUrl;

    /// <summary>Asks a public echo service for this PC's internet address. Called only from an explicit button press.</summary>
    public async Task<string?> DetectPublicIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string text = (await _publicIpClient.GetStringAsync("https://api.ipify.org", cancellationToken)
                .ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out IPAddress? address) &&
                   address.AddressFamily == AddressFamily.InterNetwork
                ? text
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>The address a phone uses from outside the home network, typed by the user (port forwarded on the router).</summary>
    public void SaveExternalUrl(string? url)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!GatewayProxyRules.TryNormalizeUpstream(url, out _))
                throw new ArgumentException("Enter a full address such as http://myhome.example.net:5118.");
            normalized = url.Trim().TrimEnd('/');
        }

        SaveConfig(Config with { ExternalUrl = normalized });
    }

    public void SaveConfig(GatewayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _store.SaveConfig(config);
    }

    public string? GatewaySecret =>
        GatewaySecretBox.Unprotect(Config.GatewaySecretProtected, _keyFile);

    public string? UpstreamAuthorization =>
        GatewaySecretBox.Unprotect(Config.UpstreamAuthorizationProtected, _keyFile);

    public Uri? UpstreamUri =>
        GatewayProxyRules.TryNormalizeUpstream(Config.UpstreamUrl, out Uri? uri) ? uri : null;

    public bool ProxyActive => GatewayProxyRules.IsActive(Config, GatewaySecret);

    public string EnsureGatewaySecret()
    {
        string? existing = GatewaySecret;
        return string.IsNullOrWhiteSpace(existing) ? GenerateGatewaySecret() : existing;
    }

    public string GenerateGatewaySecret()
    {
        string secret = GatewaySecurity.NewToken();
        SaveConfig(Config with
        {
            GatewaySecretProtected = GatewaySecretBox.Protect(secret, _keyFile),
        });
        return secret;
    }

    /// <summary>Stores proxy settings. Empty secrets mean "leave the stored value alone".</summary>
    public void SaveProxySettings(
        string? mode,
        string? upstreamUrl,
        string? upstreamAuthorization = null,
        string? gatewaySecret = null,
        bool clearAuthorization = false,
        bool clearGatewaySecret = false)
    {
        GatewayConfig config = Config;
        string normalizedMode = string.Equals(mode, GatewayConfigModes.Proxy, StringComparison.OrdinalIgnoreCase)
            ? GatewayConfigModes.Proxy
            : GatewayConfigModes.Local;

        string? authorization = config.UpstreamAuthorizationProtected;
        if (clearAuthorization) authorization = null;
        else if (!string.IsNullOrWhiteSpace(upstreamAuthorization))
            authorization = GatewaySecretBox.Protect(upstreamAuthorization.Trim(), _keyFile);

        string? secret = config.GatewaySecretProtected;
        if (clearGatewaySecret) secret = null;
        else if (!string.IsNullOrWhiteSpace(gatewaySecret))
            secret = GatewaySecretBox.Protect(gatewaySecret.Trim(), _keyFile);

        SaveConfig(config with
        {
            Mode = normalizedMode,
            UpstreamUrl = string.IsNullOrWhiteSpace(upstreamUrl) ? null : upstreamUrl.Trim(),
            UpstreamAuthorizationProtected = authorization,
            GatewaySecretProtected = secret,
        });
    }

    public async Task<UpstreamHealth> CheckUpstreamAsync(CancellationToken cancellationToken = default)
    {
        if (!ProxyActive)
            return new UpstreamHealth(false, null, 0, "Proxy mode is not active.", null);

        Uri upstream = UpstreamUri!;
        string url = upstream.ToString().TrimEnd('/') + "/health";
        long started = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage response =
                await _healthClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            long latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? version = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("version", out JsonElement value))
                    version = value.GetString();
            }
            catch (JsonException)
            {
            }

            return new UpstreamHealth(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                latency,
                response.IsSuccessStatusCode ? "Upstream reachable." : response.ReasonPhrase ?? "Upstream error.",
                version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpstreamHealth(false, null, 0, "Upstream health check timed out.", null);
        }
        catch (HttpRequestException ex)
        {
            return new UpstreamHealth(false, null, 0, ex.Message, null);
        }
    }
    public PairingOffer CreatePairing()
    {
        string token = GatewaySecurity.NewToken();
        string pin = GatewaySecurity.NewPin();
        PairingSession session = _store.CreatePairing(
            GatewaySecurity.HashToken(token),
            pin,
            "Phone",
            null,
            null,
            TimeSpan.FromMinutes(5));
        return new PairingOffer(session, token, pin);
    }

    public IReadOnlyList<PairingSession> PendingPairings() =>
        [.. Pairings().Where(value => value.Status == PairingStatuses.AwaitingApproval)];

    /// <summary>Renders a QR code as a data URL so the admin never shows a broken image.</summary>
    public static string QrDataUrl(string text, int pixelsPerModule = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        return $"data:image/png;base64,{Convert.ToBase64String(qr.GetGraphic(pixelsPerModule))}";
    }

    public PairingSession? SubmitPairing(string token, string pin, string deviceName, string? userAgent, string? ip)
    {
        PairingSession? session = _store.FindPairingByTokenHash(GatewaySecurity.HashToken(token));
        if (session is null) return null;

        if (session.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            PairingSession expired = _store.UpdatePairing(session.Id, value => value with
            {
                Status = PairingStatuses.Expired,
            })!;
            Notify(expired);
            return expired;
        }

        if (session.Status != PairingStatuses.Open)
            return session;

        if (!GatewaySecurity.VerifyPin(pin, session.PinSalt, session.PinHash))
        {
            PairingSession failed = _store.UpdatePairing(session.Id, value =>
            {
                int attempts = value.Attempts + 1;
                return value with
                {
                    Attempts = attempts,
                    Status = attempts >= 5 ? PairingStatuses.Denied : value.Status,
                };
            })!;
            Notify(failed);
            return failed;
        }

        PairingSession updated = _store.UpdatePairing(session.Id, value => value with
        {
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim(),
            UserAgent = userAgent,
            Ip = ip,
            Status = PairingStatuses.AwaitingApproval,
        })!;
        Notify(updated);
        return updated;
    }

    public PairingSession? Approve(string id, string mode)
    {
        if (mode is not (GatewayModes.Mobile or GatewayModes.Full)) return null;
        PairingSession? current = _store.FindPairing(id);
        if (current is null || current.Status != PairingStatuses.AwaitingApproval) return null;

        string token = GatewaySecurity.NewToken();
        var device = new GatewayDevice
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = current.DeviceName,
            UserAgent = current.UserAgent,
            TokenHash = GatewaySecurity.HashToken(token),
            Mode = mode,
            LastIp = current.Ip,
        };
        _store.AddDevice(device);
        _approvedTokens[id] = token;

        PairingSession updated = _store.UpdatePairing(id, value => value with
        {
            Status = PairingStatuses.Approved,
            ApprovedMode = mode,
            DeviceId = device.Id,
        })!;
        Notify(updated);
        return updated;
    }

    public PairingSession? Deny(string id)
    {
        PairingSession? updated = _store.UpdatePairing(id, value => value with
        {
            Status = PairingStatuses.Denied,
        });
        if (updated is not null) Notify(updated);
        return updated;
    }

    public PairingSession? PairingStatus(string token)
    {
        PairingSession? session = _store.FindPairingByTokenHash(GatewaySecurity.HashToken(token));
        if (session is null) return null;
        if (session.ExpiresUtc < DateTimeOffset.UtcNow &&
            session.Status is PairingStatuses.Open or PairingStatuses.AwaitingApproval)
        {
            session = _store.UpdatePairing(session.Id, value => value with
            {
                Status = PairingStatuses.Expired,
            });
            if (session is not null) Notify(session);
        }
        return session;
    }

    public string? TakeApprovedToken(string id)
    {
        return _approvedTokens.TryRemove(id, out string? token) ? token : null;
    }

    public GatewayDevice? Authenticate(string token)
    {
        GatewayDevice? device = _store.FindDeviceByTokenHash(GatewaySecurity.HashToken(token));
        return device;
    }

    public void Touch(GatewayDevice device, string? ip) => _store.TouchDevice(device.Id, ip);

    /// <summary>
    /// Creates a one-time login token for an already trusted device. The QR shows
    /// this token; redeeming it swaps in a fresh device token, so a screenshot of
    /// the code stops working after the first scan or when it expires.
    /// </summary>
    public string? IssueDeviceLogin(string deviceId, TimeSpan lifetime)
    {
        GatewayDevice? device = _store.FindDevice(deviceId);
        if (device is null || device.Revoked) return null;

        string token = GatewaySecurity.NewToken();
        return _store.SetDeviceLoginToken(
            deviceId,
            GatewaySecurity.HashToken(token),
            DateTimeOffset.UtcNow.Add(lifetime))
            ? token
            : null;
    }

    /// <summary>Exchanges a login QR token for a device cookie token. Returns null when stale or reused.</summary>
    public (string Token, string Mode)? RedeemDeviceLogin(string loginToken)
    {
        if (string.IsNullOrWhiteSpace(loginToken)) return null;

        GatewayDevice? device = _store.FindDeviceByLoginTokenHash(GatewaySecurity.HashToken(loginToken));
        if (device is null) return null;

        string deviceToken = GatewaySecurity.NewToken();
        return _store.RedeemDeviceLogin(device.Id, GatewaySecurity.HashToken(deviceToken))
            ? (deviceToken, device.Mode)
            : null;
    }

    public IReadOnlyList<GatewayDevice> Devices() =>
        [.. _store.ListDevices().Where(value => !value.Revoked)];
    public IReadOnlyList<PairingSession> Pairings() => _store.ListPairings();
    public bool Revoke(string id) => _store.RevokeDevice(id);
    public bool SetMode(string id, string mode) => _store.SetDeviceMode(id, mode);

    public IReadOnlyList<LanAddress> LanAddresses()
    {
        int port = 5118;
        if (Uri.TryCreate(_options.LanUrl, UriKind.Absolute, out Uri? lanUri)) port = lanUri.Port;
        var addresses = new List<LanAddress>();
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (network.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                network.Description.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                network.Description.Contains("vpn", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (UnicastIPAddressInformation info in network.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = info.Address.ToString();
                if (IPAddress.IsLoopback(info.Address)) continue;
                addresses.Add(new LanAddress(ip, $"http://{ip}:{port}"));
            }
        }

        return [.. addresses.DistinctBy(value => value.Address)];
    }

    public ChannelReader<PairingSession> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<PairingSession>();
        lock (_subscriberGate) _subscribers.Add(channel);
        cancellationToken.Register(() =>
        {
            lock (_subscriberGate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    public void Dispose()
    {
        _healthClient.Dispose();
        _publicIpClient.Dispose();
        lock (_subscriberGate)
        {
            foreach (Channel<PairingSession> subscriber in _subscribers)
                subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    private void Notify(PairingSession session)
    {
        lock (_subscriberGate)
        {
            foreach (Channel<PairingSession> subscriber in _subscribers)
                subscriber.Writer.TryWrite(session);
        }
    }
}

