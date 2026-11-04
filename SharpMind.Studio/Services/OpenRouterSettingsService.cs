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

    public string BaseUrl => _store.Document.BaseUrl;
    public string? DefaultModel => _store.Document.DefaultModel;
    public IReadOnlyList<string> ActiveModels => _store.Document.ActiveModels;
    public IReadOnlyList<string> RecentModels => _store.Document.RecentModels;
    public bool FreeModelsSeeded => _store.Document.FreeModelsSeeded;
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsConfigured => HasApiKey;

    public string? ApiKey => _store.ApiKey;

    public OpenRouterConnection Connection => new(
        ApiKey ?? "",
        BaseUrl,
        OpenRouterSettingsDocument.AttributionReferer,
        OpenRouterSettingsDocument.AttributionTitle);

    public void Save(string? apiKey, string? baseUrl, string? defaultModel)
    {
        if (apiKey is not null)
            _store.SetApiKey(apiKey);
        _store.SetDefaults(baseUrl, defaultModel);
        Changed?.Invoke();
    }

    public void SetDefaultModel(string? model)
    {
        _store.SetDefaultModel(model);
        Changed?.Invoke();
    }

    public bool AddActiveModel(string? model)
    {
        bool added = _store.AddActiveModel(model);
        if (added) Changed?.Invoke();
        return added;
    }

    public int AddActiveModels(IEnumerable<string> models)
    {
        int added = _store.AddActiveModels(models);
        if (added > 0) Changed?.Invoke();
        return added;
    }

    public bool RemoveActiveModel(string? model)
    {
        bool removed = _store.RemoveActiveModel(model);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void MarkFreeModelsSeeded()
    {
        _store.MarkFreeModelsSeeded();
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

    /// <summary>Fetches the public model catalog. Works before an API key is saved.</summary>
    public Task<IReadOnlyList<OpenRouterModel>> FetchModelsAsync(
        CancellationToken cancellationToken = default)
        => _client.ListModelsAsync(Connection, cancellationToken);

    /// <summary>Adds every free model in the catalog to the active list.</summary>
    public async Task<int> AddAllFreeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenRouterModel> models = await FetchModelsAsync(cancellationToken);
        int added = AddActiveModels(models.Where(model => model.IsFree).Select(model => model.Id));
        _store.MarkFreeModelsSeeded();
        Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// First-run helper for an empty chooser: adds the default model and every
    /// free model, then returns how many were added.
    /// </summary>
    public async Task<int> SeedDefaultModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenRouterModel> models = await FetchModelsAsync(cancellationToken);
        var ids = new List<string> { OpenRouterSettingsDocument.DefaultModelId };
        ids.AddRange(models.Where(model => model.IsFree).Select(model => model.Id));
        int added = AddActiveModels(ids);
        _store.MarkFreeModelsSeeded();
        Changed?.Invoke();
        return added;
    }
}
