using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Gateway;

/// <summary>
/// Small JSON-backed gateway store. Secrets are never stored raw: device and
/// pairing tokens are SHA-256 hashes, PINs are salted PBKDF2 hashes.
/// </summary>
public sealed class GatewayStore
{
    private readonly Lock _gate = new();

    public GatewayStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string DevicesFile => Path.Combine(Root, "devices.json");
    public string PairingsFile => Path.Combine(Root, "pairings.json");
    public string ConfigFile => Path.Combine(Root, "gateway.json");

    public PairingSession CreatePairing(
        string tokenHash,
        string pin,
        string deviceName,
        string? userAgent,
        string? ip,
        TimeSpan lifetime)
    {
        string salt = GatewaySecurity.NewSalt();
        var session = new PairingSession
        {
            Id = Guid.NewGuid().ToString("n"),
            TokenHash = tokenHash,
            PinHash = GatewaySecurity.HashPin(pin, salt),
            PinSalt = salt,
            DeviceName = deviceName,
            UserAgent = userAgent,
            Ip = ip,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(lifetime),
        };

        lock (_gate)
        {
            List<PairingSession> sessions = LoadPairings();
            sessions.RemoveAll(value => value.ExpiresUtc < DateTimeOffset.UtcNow.AddMinutes(-10));
            sessions.Add(session);
            SavePairings(sessions);
        }

        return session;
    }

    public PairingSession? FindPairingByTokenHash(string tokenHash)
    {
        lock (_gate)
        {
            return LoadPairings().FirstOrDefault(value =>
                value.TokenHash.Equals(tokenHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    public PairingSession? FindPairing(string id)
    {
        lock (_gate) return LoadPairings().FirstOrDefault(value => value.Id == id);
    }

    public IReadOnlyList<PairingSession> ListPairings()
    {
        lock (_gate) return [.. LoadPairings().OrderByDescending(value => value.CreatedUtc)];
    }

    public PairingSession? UpdatePairing(string id, Func<PairingSession, PairingSession> update)
    {
        lock (_gate)
        {
            List<PairingSession> sessions = LoadPairings();
            int index = sessions.FindIndex(value => value.Id == id);
            if (index < 0) return null;
            sessions[index] = update(sessions[index]);
            SavePairings(sessions);
            return sessions[index];
        }
    }

    public IReadOnlyList<GatewayDevice> ListDevices()
    {
        lock (_gate) return [.. LoadDevices().OrderByDescending(value => value.CreatedUtc)];
    }

    public GatewayDevice? FindDeviceByTokenHash(string tokenHash)
    {
        lock (_gate)
        {
            return LoadDevices().FirstOrDefault(value =>
                !value.Revoked &&
                value.TokenHash.Equals(tokenHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    public GatewayDevice? FindDevice(string id)
    {
        lock (_gate) return LoadDevices().FirstOrDefault(value => value.Id == id);
    }

    public GatewayDevice? FindDeviceByLoginTokenHash(string tokenHash)
    {
        lock (_gate)
        {
            return LoadDevices().FirstOrDefault(value =>
                !value.Revoked &&
                value.LoginExpiresUtc > DateTimeOffset.UtcNow &&
                value.LoginTokenHash is { Length: > 0 } hash &&
                hash.Equals(tokenHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Stores the hash of a one-time login QR token, or clears it when null.</summary>
    public bool SetDeviceLoginToken(string id, string? tokenHash, DateTimeOffset? expiresUtc)
    {
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            int index = devices.FindIndex(value => value.Id == id);
            if (index < 0) return false;
            devices[index] = devices[index] with
            {
                LoginTokenHash = tokenHash,
                LoginExpiresUtc = expiresUtc,
            };
            SaveDevices(devices);
            return true;
        }
    }

    /// <summary>
    /// Consumes a login QR: replaces the device token with a fresh one and clears
    /// the login hash, so the QR works exactly once.
    /// </summary>
    public bool RedeemDeviceLogin(string id, string newTokenHash)
    {
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            int index = devices.FindIndex(value => value.Id == id);
            if (index < 0) return false;
            devices[index] = devices[index] with
            {
                TokenHash = newTokenHash,
                LoginTokenHash = null,
                LoginExpiresUtc = null,
                LastSeenUtc = DateTimeOffset.UtcNow,
            };
            SaveDevices(devices);
            return true;
        }
    }

    public GatewayDevice AddDevice(GatewayDevice device)
    {
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            devices.Add(device);
            SaveDevices(devices);
            return device;
        }
    }

    public bool RevokeDevice(string id)
    {
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            int index = devices.FindIndex(value => value.Id == id);
            if (index < 0) return false;
            devices[index] = devices[index] with { Revoked = true };
            SaveDevices(devices);
            return true;
        }
    }

    public bool SetDeviceMode(string id, string mode)
    {
        if (mode is not (GatewayModes.Mobile or GatewayModes.Full)) return false;
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            int index = devices.FindIndex(value => value.Id == id);
            if (index < 0) return false;
            devices[index] = devices[index] with { Mode = mode };
            SaveDevices(devices);
            return true;
        }
    }

    public void TouchDevice(string id, string? ip)
    {
        lock (_gate)
        {
            List<GatewayDevice> devices = LoadDevices();
            int index = devices.FindIndex(value => value.Id == id);
            if (index < 0) return;
            devices[index] = devices[index] with
            {
                LastSeenUtc = DateTimeOffset.UtcNow,
                LastIp = ip,
            };
            SaveDevices(devices);
        }
    }

    public GatewayConfig LoadConfig()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(ConfigFile)) return new GatewayConfig();
                GatewayConfig config = JsonFile.Read<GatewayConfig>(ConfigFile);
                return config.Schema == GatewayConfig.CurrentSchema ? config : new GatewayConfig();
            }
            catch
            {
                return new GatewayConfig();
            }
        }
    }

    public void SaveConfig(GatewayConfig config)
    {
        lock (_gate) JsonFile.Write(ConfigFile, config with { Schema = GatewayConfig.CurrentSchema });
    }

    private List<GatewayDevice> LoadDevices()
    {
        try
        {
            return File.Exists(DevicesFile)
                ? JsonFile.Read<List<GatewayDevice>>(DevicesFile)
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveDevices(List<GatewayDevice> devices) => JsonFile.Write(DevicesFile, devices);

    private List<PairingSession> LoadPairings()
    {
        try
        {
            return File.Exists(PairingsFile)
                ? JsonFile.Read<List<PairingSession>>(PairingsFile)
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void SavePairings(List<PairingSession> pairings) => JsonFile.Write(PairingsFile, pairings);
}
