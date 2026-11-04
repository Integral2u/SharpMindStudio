using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the OpenRouter connection settings and exposes them as one immutable
/// snapshot for request building. The API key is decrypted only when read.
/// </summary>
public sealed class OpenRouterSettingsService
{
    private readonly OpenRouterSettingsStore _store;

    public OpenRouterSettingsService()
    {
        StudioPaths.EnsureCreated();
        _store = new OpenRouterSettingsStore(
            StudioPaths.OpenRouterFile,
            StudioPaths.OpenRouterKeyFile);
    }

    public event Action? Changed;

    public string BaseUrl => _store.Document.BaseUrl;
    public string? DefaultModel => _store.Document.DefaultModel;
    public string? Referer => _store.Document.Referer;
    public string? Title => _store.Document.Title;
    public IReadOnlyList<string> RecentModels => _store.Document.RecentModels;
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsConfigured => HasApiKey;

    public string? ApiKey => _store.ApiKey;

    public OpenRouterConnection Connection => new(
        ApiKey ?? "",
        BaseUrl,
        Referer,
        Title);

    public void Save(
        string? apiKey,
        string? baseUrl,
        string? defaultModel,
        string? referer,
        string? title)
    {
        if (apiKey is not null)
            _store.SetApiKey(apiKey);
        _store.SetDefaults(baseUrl, defaultModel, referer, title);
        Changed?.Invoke();
    }

    public void ClearApiKey()
    {
        _store.ClearApiKey();
        Changed?.Invoke();
    }

    public void RememberModel(string? model)
    {
        _store.RememberModel(model);
        Changed?.Invoke();
    }
}
