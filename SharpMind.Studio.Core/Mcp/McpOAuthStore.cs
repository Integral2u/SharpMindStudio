using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Mcp;

internal sealed record McpOAuthRecord
{
    public string? AccessTokenProtected { get; init; }
    public string? RefreshTokenProtected { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public string? Scope { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecretProtected { get; init; }
    public string? RegistrationAccessTokenProtected { get; init; }
    public string AuthorizationServer { get; init; } = "";
    public string TokenEndpoint { get; init; } = "";
    public string Resource { get; init; } = "";
}

internal sealed record McpOAuthDocument
{
    public const string CurrentSchema = "studio-mcp-oauth/1";

    public string Schema { get; init; } = CurrentSchema;
    public Dictionary<string, McpOAuthRecord> Servers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Encrypted per-server OAuth state. Access tokens, refresh tokens and client
/// secrets never appear in plain text on disk; the key file uses the same
/// DPAPI/AES-GCM box as the Vault and provider keys.
/// </summary>
public sealed class McpOAuthStore
{
    private readonly Lock _gate = new();
    private readonly string _keyFile;
    private McpOAuthDocument _document;

    public McpOAuthStore(string path, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(path);
        _keyFile = System.IO.Path.GetFullPath(keyFile);

        string? folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
        _document = Load();
    }

    public string Path { get; }

    public bool Contains(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return false;
        lock (_gate)
        {
            return _document.Servers.ContainsKey(serverId.Trim());
        }
    }

    public McpOAuthSession? Get(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return null;

        lock (_gate)
        {
            if (!_document.Servers.TryGetValue(serverId.Trim(), out McpOAuthRecord? record))
                return null;

            return new McpOAuthSession(
                GatewaySecretBox.Unprotect(record.AccessTokenProtected, _keyFile),
                GatewaySecretBox.Unprotect(record.RefreshTokenProtected, _keyFile),
                record.ExpiresAtUtc,
                string.IsNullOrWhiteSpace(record.TokenType) ? "Bearer" : record.TokenType,
                record.Scope,
                record.ClientId,
                GatewaySecretBox.Unprotect(record.ClientSecretProtected, _keyFile),
                GatewaySecretBox.Unprotect(
                    record.RegistrationAccessTokenProtected, _keyFile),
                record.AuthorizationServer,
                record.TokenEndpoint,
                record.Resource);
        }
    }

    public void Save(string serverId, McpOAuthSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            _document.Servers[serverId.Trim()] = new McpOAuthRecord
            {
                AccessTokenProtected = Protect(session.AccessToken),
                RefreshTokenProtected = Protect(session.RefreshToken),
                ExpiresAtUtc = session.ExpiresAtUtc,
                TokenType = string.IsNullOrWhiteSpace(session.TokenType)
                    ? "Bearer"
                    : session.TokenType,
                Scope = session.Scope,
                ClientId = session.ClientId,
                ClientSecretProtected = Protect(session.ClientSecret),
                RegistrationAccessTokenProtected = Protect(session.RegistrationAccessToken),
                AuthorizationServer = session.AuthorizationServer,
                TokenEndpoint = session.TokenEndpoint,
                Resource = session.Resource,
            };
            SaveLocked();
        }
    }

    public bool Remove(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return false;
        lock (_gate)
        {
            if (!_document.Servers.Remove(serverId.Trim())) return false;
            SaveLocked();
            return true;
        }
    }

    private string? Protect(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : GatewaySecretBox.Protect(value, _keyFile);

    private McpOAuthDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new McpOAuthDocument();

            McpOAuthDocument document = JsonFile.Read<McpOAuthDocument>(Path);
            if (document.Schema != McpOAuthDocument.CurrentSchema)
                return new McpOAuthDocument();

            return document with
            {
                Servers = new Dictionary<string, McpOAuthRecord>(
                    document.Servers ?? [], StringComparer.OrdinalIgnoreCase),
            };
        }
        catch
        {
            return new McpOAuthDocument();
        }
    }

    private void SaveLocked() => JsonFile.Write(Path, _document);
}
