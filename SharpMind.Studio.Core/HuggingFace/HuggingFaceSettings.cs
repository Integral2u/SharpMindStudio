using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.HuggingFace;

/// <summary>HuggingFace token settings. The token is encrypted at rest.</summary>
public sealed record HuggingFaceSettingsDocument
{
    public const string CurrentSchema = "studio-huggingface/1";

    public string Schema { get; init; } = CurrentSchema;
    public string? TokenProtected { get; set; }
}

public sealed class HuggingFaceSettingsStore
{
    private readonly object _gate = new();
    private readonly string _keyFile;
    private HuggingFaceSettingsDocument _document;

    public HuggingFaceSettingsStore(string settingsPath, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(settingsPath);
        _keyFile = System.IO.Path.GetFullPath(keyFile);
        _document = Load();
    }

    public string Path { get; }

    public string? ApiToken
    {
        get
        {
            lock (_gate)
                return GatewaySecretBox.Unprotect(_document.TokenProtected, _keyFile);
        }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(ApiToken);

    public void SetToken(string? token)
    {
        lock (_gate)
        {
            _document.TokenProtected = string.IsNullOrWhiteSpace(token)
                ? null
                : GatewaySecretBox.Protect(token.Trim(), _keyFile);
            SaveLocked();
        }
    }

    public void ClearToken()
    {
        lock (_gate)
        {
            _document.TokenProtected = null;
            SaveLocked();
        }
    }

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
