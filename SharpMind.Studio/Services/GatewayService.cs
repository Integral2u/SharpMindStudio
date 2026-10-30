using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using QRCoder;
using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record PairingOffer(PairingSession Session, string Token, string Pin);
public sealed record LanAddress(string Address, string Url);

public sealed class GatewayService : IDisposable
{
    public const string DeviceCookie = "sharpmind_device";

    private readonly GatewayStore _store;
    private readonly StudioOptions _options;
    private readonly ConcurrentDictionary<string, string> _approvedTokens = new();
    private readonly List<Channel<PairingSession>> _subscribers = [];
    private readonly Lock _subscriberGate = new();

    public GatewayService(IOptions<StudioOptions> options)
    {
        _options = options.Value;
        StudioPaths.EnsureCreated();
        _store = new GatewayStore(StudioPaths.Gateway);
    }

    public string DataFolder => _store.Root;
    public GatewayConfig Config => _store.LoadConfig();

    public void SaveLanEnabled(bool enabled)
    {
        GatewayConfig config = _store.LoadConfig();
        _store.SaveConfig(config with { LanEnabled = enabled });
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

    public IReadOnlyList<GatewayDevice> Devices() => _store.ListDevices();
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

