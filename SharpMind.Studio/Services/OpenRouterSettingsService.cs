using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the remote provider settings and exposes each provider as one immutable
/// snapshot for request building. The API keys are decrypted only when read.
/// The parameterless members keep addressing OpenRouter for existing callers;
/// the <c>*For</c> members take a <see cref="RemoteProviderPreset.Id"/>.
/// </summary>
public sealed class OpenRouterSettingsService
{
    private readonly OpenRouterSettingsStore _store;
    private readonly OpenRouterClient _client;

    public OpenRouterSettingsService(OpenRouterClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        StudioPaths.EnsureCreated();
        _store = new OpenRouterSettingsStore(
            StudioPaths.OpenRouterFile,
            StudioPaths.OpenRouterKeyFile);
    }

    public event Action? Changed;

    public IReadOnlyList<RemoteProviderPreset> Providers => RemoteProviders.All;

    public RemoteProviderPreset Preset(string? providerId) => RemoteProviders.Find(providerId);

    // OpenRouter defaults, kept for the existing chat and settings callers.
    public string BaseUrl => BaseUrlFor(RemoteProviders.OpenRouter);
    public string? DefaultModel => DefaultModelFor(RemoteProviders.OpenRouter);
    public IReadOnlyList<string> ActiveModels => ActiveModelsFor(RemoteProviders.OpenRouter);
    public IReadOnlyList<string> RecentModels => RecentModelsFor(RemoteProviders.OpenRouter);
    public bool FreeModelsSeeded => FreeModelsSeededFor(RemoteProviders.OpenRouter);
    public bool HasApiKey => HasApiKeyFor(RemoteProviders.OpenRouter);
    public bool IsConfigured => IsConfiguredFor(RemoteProviders.OpenRouter);
    public string? ApiKey => ApiKeyFor(RemoteProviders.OpenRouter);
    public OpenRouterConnection Connection => ConnectionFor(RemoteProviders.OpenRouter);

    public string BaseUrlFor(string? providerId) => _store.Profile(providerId).BaseUrl;
    public string? DefaultModelFor(string? providerId) => _store.Profile(providerId).DefaultModel;
    public IReadOnlyList<string> ActiveModelsFor(string? providerId) =>
        _store.Profile(providerId).ActiveModels;
    public IReadOnlyList<string> RecentModelsFor(string? providerId) =>
        _store.Profile(providerId).RecentModels;
    public bool FreeModelsSeededFor(string? providerId) =>
        _store.Profile(providerId).FreeModelsSeeded;
    public bool HasApiKeyFor(string? providerId) =>
        !string.IsNullOrWhiteSpace(ApiKeyFor(providerId));
    public bool IsConfiguredFor(string? providerId) => _store.IsConfigured(providerId);
    public string? ApiKeyFor(string? providerId) => _store.GetApiKey(providerId);

    public OpenRouterConnection ConnectionFor(string? providerId)
    {
        RemoteProviderPreset preset = RemoteProviders.Find(providerId);
        return new OpenRouterConnection(
            ApiKeyFor(preset.Id) ?? "",
            BaseUrlFor(preset.Id),
            preset.AttributionHeaders ? OpenRouterSettingsDocument.AttributionReferer : null,
            preset.AttributionHeaders ? OpenRouterSettingsDocument.AttributionTitle : null);
    }

    public void Save(string? apiKey, string? baseUrl, string? defaultModel) =>
        SaveFor(RemoteProviders.OpenRouter, apiKey, baseUrl, defaultModel);

    public void SaveFor(
        string? providerId,
        string? apiKey,
        string? baseUrl,
        string? defaultModel)
    {
        if (apiKey is not null)
            _store.SetApiKey(providerId, apiKey);
        _store.SetDefaults(providerId, baseUrl, defaultModel);
        Changed?.Invoke();
    }

    public void SetDefaultModel(string? model) =>
        SetDefaultModelFor(RemoteProviders.OpenRouter, model);

    public void SetDefaultModelFor(string? providerId, string? model)
    {
        _store.SetDefaultModel(providerId, model);
        Changed?.Invoke();
    }

    public bool AddActiveModel(string? model) =>
        AddActiveModelFor(RemoteProviders.OpenRouter, model);

    public bool AddActiveModelFor(string? providerId, string? model)
    {
        bool added = _store.AddActiveModel(providerId, model);
        if (added) Changed?.Invoke();
        return added;
    }

    public int AddActiveModels(IEnumerable<string> models) =>
        AddActiveModelsFor(RemoteProviders.OpenRouter, models);

    public int AddActiveModelsFor(string? providerId, IEnumerable<string> models)
    {
        int added = _store.AddActiveModels(providerId, models);
        if (added > 0) Changed?.Invoke();
        return added;
    }

    public bool RemoveActiveModel(string? model) =>
        RemoveActiveModelFor(RemoteProviders.OpenRouter, model);

    public bool RemoveActiveModelFor(string? providerId, string? model)
    {
        bool removed = _store.RemoveActiveModel(providerId, model);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void MarkFreeModelsSeeded() =>
        MarkFreeModelsSeededFor(RemoteProviders.OpenRouter);

    public void MarkFreeModelsSeededFor(string? providerId)
    {
        _store.MarkFreeModelsSeeded(providerId);
        Changed?.Invoke();
    }

    public void ClearApiKey() => ClearApiKeyFor(RemoteProviders.OpenRouter);

    public void ClearApiKeyFor(string? providerId)
    {
        _store.ClearApiKey(providerId);
        Changed?.Invoke();
    }

    public void RememberModel(string? model) =>
        RememberModelFor(RemoteProviders.OpenRouter, model);

    public void RememberModelFor(string? providerId, string? model)
    {
        _store.RememberModel(providerId, model);
        Changed?.Invoke();
    }

    /// <summary>Fetches the public model catalog. Works before an API key is saved.</summary>
    public Task<IReadOnlyList<OpenRouterModel>> FetchModelsAsync(
        CancellationToken cancellationToken = default) =>
        FetchModelsForAsync(RemoteProviders.OpenRouter, cancellationToken);

    public Task<IReadOnlyList<OpenRouterModel>> FetchModelsForAsync(
        string? providerId,
        CancellationToken cancellationToken = default) =>
        _client.ListModelsAsync(ConnectionFor(providerId), cancellationToken);

    /// <summary>Adds every free model in the catalog to the active list.</summary>
    public Task<int> AddAllFreeAsync(CancellationToken cancellationToken = default) =>
        AddAllFreeForAsync(RemoteProviders.OpenRouter, cancellationToken);

    public async Task<int> AddAllFreeForAsync(
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenRouterModel> models =
            await FetchModelsForAsync(providerId, cancellationToken).ConfigureAwait(false);
        int added = AddActiveModelsFor(providerId,
            models.Where(model => model.IsFree).Select(model => model.Id));
        _store.MarkFreeModelsSeeded(providerId);
        Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// First-run helper for an empty chooser: adds the provider's default model
    /// (when it has one) and every free model.
    /// </summary>
    public Task<int> SeedDefaultModelsAsync(CancellationToken cancellationToken = default) =>
        SeedDefaultModelsForAsync(RemoteProviders.OpenRouter, cancellationToken);

    public async Task<int> SeedDefaultModelsForAsync(
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        RemoteProviderPreset preset = RemoteProviders.Find(providerId);
        IReadOnlyList<OpenRouterModel> models =
            await FetchModelsForAsync(providerId, cancellationToken).ConfigureAwait(false);

        var ids = new List<string>();
        if (preset.DefaultModelId is { } defaultId) ids.Add(defaultId);
        ids.AddRange(models.Where(model => model.IsFree).Select(model => model.Id));

        int added = AddActiveModelsFor(providerId, ids);
        _store.MarkFreeModelsSeeded(providerId);
        Changed?.Invoke();
        return added;
    }

    /// <summary>Validates the saved key against the provider without spending credit.</summary>
    public Task<RemoteKeyInfo> TestKeyAsync(
        string? providerId,
        CancellationToken cancellationToken = default) =>
        _client.GetKeyInfoAsync(ConnectionFor(providerId), cancellationToken);
}
