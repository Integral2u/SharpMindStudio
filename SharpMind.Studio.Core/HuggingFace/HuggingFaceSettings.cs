using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.HuggingFace;

/// <summary>HuggingFace token settings. The token is encrypted at rest.</summary>
public sealed record HuggingFaceSettingsDocument
{
    public const string CurrentSchema = "studio-huggingface/1";

    public string Schema { get; init; } = CurrentSchema;
    public string? TokenProtected { get; set; }

    /// <summary>A Vault secret name. When set it wins over <see cref="TokenProtected"/>.</summary>
    public string? TokenSecret { get; set; }
}

public sealed class HuggingFaceSettingsStore
{
    private readonly object _gate = new();
    private readonly string _keyFile;
    private readonly ISecretResolver _secrets;
    private HuggingFaceSettingsDocument _document;

    public HuggingFaceSettingsStore(
        string settingsPath,
        string keyFile,
        ISecretResolver? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(settingsPath);
        _keyFile = System.IO.Path.GetFullPath(keyFile);
        _secrets = secrets ?? NullSecretResolver.Instance;
        _document = Load();
    }

    public string Path { get; }

    public string? ApiToken
    {
        get
        {
            lock (_gate) return ResolveTokenLocked();
        }
    }

    public string? TokenSecret
    {
        get
        {
            lock (_gate) return _document.TokenSecret;
        }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(ApiToken);

    public void SetToken(string? token)
    {
        lock (_gate)
        {
            _document.TokenSecret = null;
            _document.TokenProtected = string.IsNullOrWhiteSpace(token)
                ? null
                : GatewaySecretBox.Protect(token.Trim(), _keyFile);
            SaveLocked();
        }
    }

    /// <summary>Uses a named Vault secret and drops the inline ciphertext.</summary>
    public void SetTokenSecret(string? secretName)
    {
        lock (_gate)
        {
            _document.TokenSecret = string.IsNullOrWhiteSpace(secretName)
                ? null
                : secretName.Trim();
            _document.TokenProtected = null;
            SaveLocked();
        }
    }

    public void ClearToken()
    {
        lock (_gate)
        {
            _document.TokenProtected = null;
            _document.TokenSecret = null;
            SaveLocked();
        }
    }

    private string? ResolveTokenLocked() =>
        !string.IsNullOrWhiteSpace(_document.TokenSecret)
            ? _secrets.Resolve(_document.TokenSecret)
            : GatewaySecretBox.Unprotect(_document.TokenProtected, _keyFile);

    private HuggingFaceSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new HuggingFaceSettingsDocument();
            HuggingFaceSettingsDocument document = JsonFile.Read<HuggingFaceSettingsDocument>(Path);
            return document.Schema == HuggingFaceSettingsDocument.CurrentSchema
                ? document
                : new HuggingFaceSettingsDocument();
        }
        catch
        {
            return new HuggingFaceSettingsDocument();
        }
    }

    private void SaveLocked()
    {
        try
        {
            JsonFile.Write(Path, _document);
        }
        catch
        {
        }
    }
}
