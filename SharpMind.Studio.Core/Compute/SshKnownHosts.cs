using System.Security.Cryptography;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Compute;

/// <summary>How a presented SSH host key relates to the saved one.</summary>
public enum SshHostKeyStatus
{
    /// <summary>First connection to this host; the key was saved.</summary>
    New,
    /// <summary>The key matches the saved fingerprint.</summary>
    Known,
    /// <summary>The key differs from the saved fingerprint.</summary>
    Changed,
}

/// <summary>One saved SSH host key, keyed by host and port.</summary>
public sealed class SshKnownHost
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string KeyType { get; init; }
    public required string Fingerprint { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class SshKnownHostsDocument
{
    public const string CurrentSchema = "studio-ssh-known-hosts/1";

    public string Schema { get; init; } = CurrentSchema;
    public Dictionary<string, SshKnownHost> Hosts { get; set; } = [];
}

/// <summary>
/// Trust-on-first-use host keys for the SSH setup helper. The first successful
/// connection records the host key's SHA-256 fingerprint; later connections must
/// match it. A rebuilt host presents a new key, so the user has to forget the
/// saved key explicitly before it is trusted again.
/// </summary>
public sealed class SshKnownHosts
{
    private readonly object _gate = new();
    private readonly string _path;
    private SshKnownHostsDocument _document;

    public SshKnownHosts(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        _document = Load();
    }

    public static SshKnownHosts Default { get; } = new(
        System.IO.Path.Combine(StudioPaths.Compute, "ssh-known-hosts.json"));

    public string Path => _path;

    public SshHostKeyStatus Verify(
        string host,
        int port,
        string keyType,
        string fingerprint,
        out SshKnownHost? known)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            string key = Key(host, port);
            if (_document.Hosts.TryGetValue(key, out known))
            {
                return string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? SshHostKeyStatus.Known
                    : SshHostKeyStatus.Changed;
            }

            known = new SshKnownHost
            {
                Host = host.Trim(),
                Port = port,
                KeyType = keyType,
                Fingerprint = fingerprint,
            };
            _document.Hosts[key] = known;
            SaveLocked();
            return SshHostKeyStatus.New;
        }
    }

    public SshKnownHost? Find(string host, int port)
    {
        lock (_gate)
            return _document.Hosts.GetValueOrDefault(Key(host, port));
    }

    public bool Forget(string host, int port)
    {
        lock (_gate)
        {
            if (!_document.Hosts.Remove(Key(host, port))) return false;
            SaveLocked();
            return true;
        }
    }

    /// <summary>OpenSSH-style SHA-256 fingerprint, without base64 padding.</summary>
    public static string Fingerprint(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] hash = SHA256.HashData(key);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private static string Key(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{port}";

    private SshKnownHostsDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SshKnownHostsDocument();
            SshKnownHostsDocument document = JsonFile.Read<SshKnownHostsDocument>(_path);
            if (document.Schema != SshKnownHostsDocument.CurrentSchema)
                return new SshKnownHostsDocument();
            document.Hosts ??= [];
            return document;
        }
        catch
        {
            return new SshKnownHostsDocument();
        }
    }

    private void SaveLocked()
    {
        JsonFile.Write(_path, _document);
    }
}
