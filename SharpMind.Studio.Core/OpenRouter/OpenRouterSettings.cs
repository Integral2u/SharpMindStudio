using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>
/// OpenRouter connection settings. The API key is never stored in plain text:
/// <see cref="ApiKeyProtected"/> is AES-GCM ciphertext bound to a key file next
/// to the settings, using the same secret box as the Gateway.
/// </summary>
public sealed record OpenRouterSettingsDocument
{
    public const string CurrentSchema = "studio-openrouter/1";
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    public string Schema { get; init; } = CurrentSchema;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string? DefaultModel { get; set; }
    public string? ApiKeyProtected { get; set; }
    public string? Referer { get; set; }
    public string? Title { get; set; } = "SharpMind Studio";
    public List<string> RecentModels { get; set; } = [];
}

/// <summary>JSON-backed OpenRouter settings with an encrypted API key.</summary>
public sealed class OpenRouterSettingsStore
{
    private readonly object _gate = new();
    private readonly string _keyFile;
    private OpenRouterSettingsDocument _document;

    public OpenRouterSettingsStore(string settingsPath, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(settingsPath);
        _keyFile = System.IO.Path.GetFullPath(keyFile);
        _document = Load();
    }

    public string Path { get; }

    public OpenRouterSettingsDocument Document
    {
        get { lock (_gate) return _document; }
    }

    public string? ApiKey
    {
        get
        {
            lock (_gate)
                return GatewaySecretBox.Unprotect(_document.ApiKeyProtected, _keyFile);
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(_document.BaseUrl);

    public void SetApiKey(string? apiKey)
    {
        lock (_gate)
        {
            _document.ApiKeyProtected = string.IsNullOrWhiteSpace(apiKey)
                ? null
                : GatewaySecretBox.Protect(apiKey.Trim(), _keyFile);
            SaveLocked();
        }
    }

    public void SetDefaults(string? baseUrl, string? defaultModel, string? referer, string? title)
    {
        lock (_gate)
        {
            _document.BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? OpenRouterSettingsDocument.DefaultBaseUrl
                : NormalizeBaseUrl(baseUrl);
            _document.DefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim();
            _document.Referer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim();
            _document.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            SaveLocked();
        }
    }

    public void RememberModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        lock (_gate)
        {
            string clean = model.Trim();
            _document.RecentModels.RemoveAll(value => value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            _document.RecentModels.Insert(0, clean);
            if (_document.RecentModels.Count > 20)
                _document.RecentModels.RemoveRange(20, _document.RecentModels.Count - 20);
            SaveLocked();
        }
    }

    public void ClearApiKey()
    {
        lock (_gate)
        {
            _document.ApiKeyProtected = null;
            SaveLocked();
        }
    }

    private static string NormalizeBaseUrl(string url)
    {
        string value = url.Trim().TrimEnd('/');
        if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            return value + "/v1";
        if (value.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return value;
        return value + "/api/v1";
    }

    private OpenRouterSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new OpenRouterSettingsDocument();

            OpenRouterSettingsDocument document = JsonFile.Read<OpenRouterSettingsDocument>(Path);
            if (document.Schema != OpenRouterSettingsDocument.CurrentSchema)
                return new OpenRouterSettingsDocument();

            document.BaseUrl = string.IsNullOrWhiteSpace(document.BaseUrl)
                ? OpenRouterSettingsDocument.DefaultBaseUrl
                : NormalizeBaseUrl(document.BaseUrl);
            document.RecentModels ??= [];
            return document;
        }
        catch
        {
            return new OpenRouterSettingsDocument();
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

/// <summary>Resolved connection values handed to the streaming client.</summary>
public sealed record OpenRouterConnection(
    string ApiKey,
    string BaseUrl,
    string? Referer = null,
    string? Title = null)
{
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>Convenience accessors for callers that do not want to touch the store.</summary>
public static class OpenRouterPaths
{
    public static string SettingsFile => StudioPaths.OpenRouterFile;
    public static string KeyFile => StudioPaths.OpenRouterKeyFile;
}
