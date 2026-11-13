using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>One provider's saved connection, key and model selection.</summary>
public sealed record RemoteProviderProfile
{
    public string BaseUrl { get; set; } = "";
    public string? ApiKeyProtected { get; set; }
    public string? DefaultModel { get; set; }

    /// <summary>Models shown in the Chat/Work chooser and the Settings default picker.</summary>
    public List<string> ActiveModels { get; set; } = [];

    /// <summary>Recently used models (kept for older settings files and suggestions).</summary>
    public List<string> RecentModels { get; set; } = [];

    /// <summary>True after the first free-model seed so setup does not repeat it.</summary>
    public bool FreeModelsSeeded { get; set; }

    /// <summary>True once the old auto-seeded gpt-4o-mini entry has been removed.</summary>
    public bool LegacyDefaultRemoved { get; set; }
}

/// <summary>
/// Remote provider settings. Each provider keeps its own key, base URL and
/// model lists. The API key is never stored in plain text: it is AES-GCM
/// ciphertext bound to a key file next to the settings, using the same secret
/// box as the Gateway. The legacy single-provider layout from
/// <c>studio-openrouter/1</c> is migrated into the OpenRouter profile on load.
/// </summary>
public sealed record OpenRouterSettingsDocument
{
    public const string CurrentSchema = "studio-remote-providers/1";
    public const string LegacySchema = "studio-openrouter/1";
    public const string DefaultBaseUrl = RemoteProviders.OpenRouterDefaultBaseUrl;
    public const string DefaultModelId = "openrouter/free";
    public const string AttributionReferer = "https://integral2u.github.io/SharpMindStudio/";
    public const string AttributionTitle = "SharpMind Studio";

    public string Schema { get; init; } = CurrentSchema;

    /// <summary>Per-provider profiles keyed by <see cref="RemoteProviderPreset.Id"/>.</summary>
    public Dictionary<string, RemoteProviderProfile> Providers { get; set; } = [];

    // Legacy flat fields from studio-openrouter/1. They are migrated into the
    // OpenRouter profile and cleared, so they are never written again.
    public string? BaseUrl { get; set; }
    public string? DefaultModel { get; set; }
    public string? ApiKeyProtected { get; set; }
    public List<string>? ActiveModels { get; set; }
    public List<string>? RecentModels { get; set; }
    public bool? FreeModelsSeeded { get; set; }
    public bool? LegacyDefaultRemoved { get; set; }
}

/// <summary>JSON-backed remote provider settings with encrypted API keys.</summary>
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

    public RemoteProviderProfile Profile(string? providerId)
    {
        lock (_gate) return GetProfileLocked(providerId);
    }

    public string? GetApiKey(string? providerId)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            return GatewaySecretBox.Unprotect(profile.ApiKeyProtected, _keyFile);
        }
    }

    public bool IsConfigured(string? providerId)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            return !string.IsNullOrWhiteSpace(
                    GatewaySecretBox.Unprotect(profile.ApiKeyProtected, _keyFile)) &&
                !string.IsNullOrWhiteSpace(profile.BaseUrl);
        }
    }

    public void SetApiKey(string? providerId, string? apiKey)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            profile.ApiKeyProtected = string.IsNullOrWhiteSpace(apiKey)
                ? null
                : GatewaySecretBox.Protect(apiKey.Trim(), _keyFile);
            SaveLocked();
        }
    }

    public void SetDefaults(string? providerId, string? baseUrl, string? defaultModel)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            RemoteProviderPreset preset = RemoteProviders.Find(providerId);
            profile.BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? preset.DefaultBaseUrl
                : NormalizeBaseUrl(baseUrl);
            SetDefaultModelLocked(profile, defaultModel);
            SaveLocked();
        }
    }

    public bool AddActiveModel(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            string clean = model.Trim();
            if (profile.ActiveModels.Any(value =>
                    value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            profile.ActiveModels.Add(clean);
            if (string.IsNullOrWhiteSpace(profile.DefaultModel))
                profile.DefaultModel = clean;
            SaveLocked();
            return true;
        }
    }

    public int AddActiveModels(string? providerId, IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            int added = 0;
            foreach (string model in models)
            {
                if (string.IsNullOrWhiteSpace(model)) continue;
                string clean = model.Trim();
                if (profile.ActiveModels.Any(value =>
                        value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                    continue;
                profile.ActiveModels.Add(clean);
                added++;
            }

            if (added > 0)
            {
                if (string.IsNullOrWhiteSpace(profile.DefaultModel))
                    profile.DefaultModel = profile.ActiveModels[0];
                SaveLocked();
            }
            return added;
        }
    }

    public bool RemoveActiveModel(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            string clean = model.Trim();
            int removed = profile.ActiveModels.RemoveAll(value =>
                value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            if (string.Equals(profile.DefaultModel, clean, StringComparison.OrdinalIgnoreCase))
                profile.DefaultModel = profile.ActiveModels.FirstOrDefault();
            SaveLocked();
            return true;
        }
    }

    public void SetDefaultModel(string? providerId, string? model)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            SetDefaultModelLocked(profile, model);
            SaveLocked();
        }
    }

    public void RememberModel(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            string clean = model.Trim();
            if (!profile.ActiveModels.Any(value =>
                    value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                profile.ActiveModels.Add(clean);
            }

            profile.RecentModels.RemoveAll(value =>
                value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            profile.RecentModels.Insert(0, clean);
            if (profile.RecentModels.Count > 20)
                profile.RecentModels.RemoveRange(20, profile.RecentModels.Count - 20);

            if (string.IsNullOrWhiteSpace(profile.DefaultModel))
                profile.DefaultModel = clean;
            SaveLocked();
        }
    }

    public void ClearApiKey(string? providerId)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            profile.ApiKeyProtected = null;
            SaveLocked();
        }
    }

    public void MarkFreeModelsSeeded(string? providerId)
    {
        lock (_gate)
        {
            RemoteProviderProfile profile = GetProfileLocked(providerId);
            if (profile.FreeModelsSeeded) return;
            profile.FreeModelsSeeded = true;
            SaveLocked();
        }
    }

    private RemoteProviderProfile GetProfileLocked(string? providerId)
    {
        string id = RemoteProviders.NormalizeId(providerId);
        if (_document.Providers.TryGetValue(id, out RemoteProviderProfile? profile))
            return profile;

        RemoteProviderPreset preset = RemoteProviders.Find(id);
        profile = new RemoteProviderProfile
        {
            BaseUrl = NormalizeBaseUrl(preset.DefaultBaseUrl),
            DefaultModel = preset.DefaultModelId,
            ActiveModels = preset.DefaultModelId is { } model ? [model] : [],
        };
        _document.Providers[id] = profile;
        SaveLocked();
        return profile;
    }

    private static void SetDefaultModelLocked(RemoteProviderProfile profile, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)
            && profile.ActiveModels.Any(value =>
                value.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            profile.DefaultModel = model.Trim();
            return;
        }

        profile.DefaultModel = profile.ActiveModels.FirstOrDefault();
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

            OpenRouterSettingsDocument document =
                JsonFile.Read<OpenRouterSettingsDocument>(Path);
            return NormalizeDocument(document);
        }
        catch
        {
            return NormalizeDocument(new OpenRouterSettingsDocument());
        }
    }

    private static OpenRouterSettingsDocument NormalizeDocument(
        OpenRouterSettingsDocument document)
    {
        bool hasLegacyValues = document.BaseUrl is not null ||
                               document.DefaultModel is not null ||
                               document.ApiKeyProtected is not null ||
                               document.ActiveModels is not null ||
                               document.RecentModels is not null ||
                               document.FreeModelsSeeded is not null ||
                               document.LegacyDefaultRemoved is not null;

        if (document.Schema == OpenRouterSettingsDocument.LegacySchema ||
            (document.Providers.Count == 0 && hasLegacyValues))
        {
            MigrateLegacy(document);
            document = new OpenRouterSettingsDocument { Providers = document.Providers };
        }
        else if (document.Schema != OpenRouterSettingsDocument.CurrentSchema)
        {
            document = new OpenRouterSettingsDocument { Providers = document.Providers };
        }

        document.Providers ??= [];
        foreach (RemoteProviderPreset preset in RemoteProviders.All)
        {
            if (document.Providers.ContainsKey(preset.Id)) continue;
            document.Providers[preset.Id] = new RemoteProviderProfile
            {
                BaseUrl = preset.DefaultBaseUrl,
                DefaultModel = preset.DefaultModelId,
                ActiveModels = preset.DefaultModelId is { } model ? [model] : [],
            };
        }

        foreach (string id in document.Providers.Keys.ToArray())
            NormalizeProfile(id, document.Providers[id]);

        return document;
    }

    private static void MigrateLegacy(OpenRouterSettingsDocument document)
    {
        document.Providers[RemoteProviders.OpenRouter] = new RemoteProviderProfile
        {
            BaseUrl = string.IsNullOrWhiteSpace(document.BaseUrl)
                ? RemoteProviders.OpenRouterPreset.DefaultBaseUrl
                : NormalizeBaseUrl(document.BaseUrl),
            ApiKeyProtected = document.ApiKeyProtected,
            DefaultModel = document.DefaultModel,
            ActiveModels = document.ActiveModels ?? [],
            RecentModels = document.RecentModels ?? [],
            FreeModelsSeeded = document.FreeModelsSeeded ?? false,
            LegacyDefaultRemoved = document.LegacyDefaultRemoved ?? false,
        };

        document.BaseUrl = null;
        document.DefaultModel = null;
        document.ApiKeyProtected = null;
        document.ActiveModels = null;
        document.RecentModels = null;
        document.FreeModelsSeeded = null;
        document.LegacyDefaultRemoved = null;
    }

    private static void NormalizeProfile(string id, RemoteProviderProfile profile)
    {
        RemoteProviderPreset preset = RemoteProviders.Find(id);
        profile.BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? preset.DefaultBaseUrl
            : NormalizeBaseUrl(profile.BaseUrl);
        profile.ActiveModels ??= [];
        profile.RecentModels ??= [];

        if (preset.Id == RemoteProviders.OpenRouter && !profile.LegacyDefaultRemoved)
        {
            profile.ActiveModels.RemoveAll(model =>
                model.Equals("openai/gpt-4o-mini", StringComparison.OrdinalIgnoreCase));
            profile.LegacyDefaultRemoved = true;
        }

        if (profile.ActiveModels.Count == 0 && preset.DefaultModelId is { } defaultId)
            profile.ActiveModels.Add(defaultId);

        if (string.IsNullOrWhiteSpace(profile.DefaultModel)
            || !profile.ActiveModels.Any(value =>
                value.Equals(profile.DefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            profile.DefaultModel = profile.ActiveModels.FirstOrDefault();
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
