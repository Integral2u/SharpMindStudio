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
    public const string DefaultModelId = "openai/gpt-4o-mini";
    public const string AttributionReferer = "https://integral2u.github.io/SharpMindStudio/";
    public const string AttributionTitle = "SharpMind Studio";

    public string Schema { get; init; } = CurrentSchema;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string? DefaultModel { get; set; }
    public string? ApiKeyProtected { get; set; }

    /// <summary>Models shown in the Chat/Work chooser and the Settings default picker.</summary>
    public List<string> ActiveModels { get; set; } = [];

    /// <summary>True after the first free-model seed so setup does not repeat it.</summary>
    public bool FreeModelsSeeded { get; set; }

    /// <summary>Recently used models (kept for older settings files and suggestions).</summary>
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

    public void SetDefaults(string? baseUrl, string? defaultModel)
    {
        lock (_gate)
        {
            _document.BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? OpenRouterSettingsDocument.DefaultBaseUrl
                : NormalizeBaseUrl(baseUrl);
            SetDefaultModelLocked(defaultModel);
            SaveLocked();
        }
    }

    public bool AddActiveModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            string clean = model.Trim();
            if (_document.ActiveModels.Any(value =>
                    value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _document.ActiveModels.Add(clean);
            if (string.IsNullOrWhiteSpace(_document.DefaultModel))
                _document.DefaultModel = clean;
            SaveLocked();
            return true;
        }
    }

    public int AddActiveModels(IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        lock (_gate)
        {
            int added = 0;
            foreach (string model in models)
            {
                if (string.IsNullOrWhiteSpace(model)) continue;
                string clean = model.Trim();
                if (_document.ActiveModels.Any(value =>
                        value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _document.ActiveModels.Add(clean);
                added++;
            }

            if (added > 0)
            {
                if (string.IsNullOrWhiteSpace(_document.DefaultModel))
                    _document.DefaultModel = _document.ActiveModels[0];
                SaveLocked();
            }
            return added;
        }
    }

    public bool RemoveActiveModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            string clean = model.Trim();
            int removed = _document.ActiveModels.RemoveAll(value =>
                value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            if (string.Equals(_document.DefaultModel, clean, StringComparison.OrdinalIgnoreCase))
                _document.DefaultModel = _document.ActiveModels.FirstOrDefault();
            SaveLocked();
            return true;
        }
    }

    public void SetDefaultModel(string? model)
    {
        lock (_gate)
        {
            SetDefaultModelLocked(model);
            SaveLocked();
        }
    }

    public void RememberModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        lock (_gate)
        {
            string clean = model.Trim();
            if (!_document.ActiveModels.Any(value =>
                    value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                _document.ActiveModels.Add(clean);
            }

            _document.RecentModels.RemoveAll(value =>
                value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            _document.RecentModels.Insert(0, clean);
            if (_document.RecentModels.Count > 20)
                _document.RecentModels.RemoveRange(20, _document.RecentModels.Count - 20);

            if (string.IsNullOrWhiteSpace(_document.DefaultModel))
                _document.DefaultModel = clean;
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

    public void MarkFreeModelsSeeded()
    {
        lock (_gate)
        {
            if (_document.FreeModelsSeeded) return;
            _document.FreeModelsSeeded = true;
            SaveLocked();
        }
    }

    private void SetDefaultModelLocked(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)
            && _document.ActiveModels.Any(value =>
                value.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            _document.DefaultModel = model.Trim();
            return;
        }

        _document.DefaultModel = _document.ActiveModels.FirstOrDefault();
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
            if (!File.Exists(Path)) return NormalizeDocument(new OpenRouterSettingsDocument());

            OpenRouterSettingsDocument document = JsonFile.Read<OpenRouterSettingsDocument>(Path);
            if (document.Schema != OpenRouterSettingsDocument.CurrentSchema)
                return NormalizeDocument(new OpenRouterSettingsDocument());

            return NormalizeDocument(document);
        }
        catch
        {
            return NormalizeDocument(new OpenRouterSettingsDocument());
        }
    }

    private static OpenRouterSettingsDocument NormalizeDocument(OpenRouterSettingsDocument document)
    {
        document.BaseUrl = string.IsNullOrWhiteSpace(document.BaseUrl)
            ? OpenRouterSettingsDocument.DefaultBaseUrl
            : NormalizeBaseUrl(document.BaseUrl);
        document.ActiveModels ??= [];
        document.RecentModels ??= [];

        if (document.ActiveModels.Count == 0)
            document.ActiveModels.Add(OpenRouterSettingsDocument.DefaultModelId);

        if (string.IsNullOrWhiteSpace(document.DefaultModel)
            || !document.ActiveModels.Any(value =>
                value.Equals(document.DefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            document.DefaultModel = document.ActiveModels[0];
        }

        return document;
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
    string? Referer = OpenRouterSettingsDocument.AttributionReferer,
    string? Title = OpenRouterSettingsDocument.AttributionTitle)
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
