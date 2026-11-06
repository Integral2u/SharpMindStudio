using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the SharpMind Compute hosts configured in Settings → Providers and
/// exposes them as resolved connections. Tokens are decrypted only when a host
/// is used; the UI sees masked state.
/// </summary>
public sealed class ComputeProviderService
{
    private readonly ComputeSettingsStore _store;
    private readonly ComputeClient _client;

    public ComputeProviderService(ComputeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        StudioPaths.EnsureCreated();
        _store = new ComputeSettingsStore(StudioPaths.ComputeFile, StudioPaths.ComputeKeyFile);
    }

    public event Action? Changed;

    public IReadOnlyList<ComputeProviderSettings> Providers => _store.Providers;
    public string? DefaultProviderId => _store.DefaultProviderId;
    public ComputeProviderSettings? DefaultProvider => _store.Find(_store.DefaultProviderId);
    public string SettingsFile => _store.Path;

    /// <summary>The last image pushed from the Publish panel, if any.</summary>
    public string? RegistryImage => _store.RegistryImage;

    public void SetRegistryImage(string? image)
    {
        _store.SetRegistryImage(image);
        Changed?.Invoke();
    }

    public ComputeProviderSettings? Find(string? id) => _store.Find(id);

    public ComputeProvider? Resolve(string? id) => _store.Resolve(id);

    public ComputeProviderSettings Add(
        string name,
        string? kind,
        string? baseUrl,
        string? token,
        string? prefab = null)
    {
        ComputeProviderSettings provider = _store.Add(name, kind, baseUrl, token, prefab);
        Changed?.Invoke();
        return provider;
    }

    public bool Update(
        string id,
        string name,
        string? kind,
        string? baseUrl,
        string? token,
        string? defaultModel = null,
        string? prefab = null)
    {
        bool updated = _store.Update(id, name, kind, baseUrl, token, defaultModel, prefab);
        if (updated) Changed?.Invoke();
        return updated;
    }

    public bool Remove(string id)
    {
        bool removed = _store.Remove(id);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool SetDefault(string? id)
    {
        bool updated = _store.SetDefault(id);
        if (updated) Changed?.Invoke();
        return updated;
    }

    public bool SetDefaultModel(string id, string? model)
    {
        _store.SetDefaultModel(id, model);
        Changed?.Invoke();
        return true;
    }

    public void RememberModel(string id, string? model)
    {
        if (_store.RememberModel(id, model)) Changed?.Invoke();
    }

    public bool ForgetModel(string id, string? model)
    {
        bool removed = _store.ForgetModel(id, model);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool HasToken(string id) => !string.IsNullOrWhiteSpace(_store.Token(id));

    public string? Token(string id) => _store.Token(id);

    public void SetToken(string id, string? token)
    {
        if (_store.SetToken(id, token)) Changed?.Invoke();
    }

    /// <summary>Returns the saved token, or creates and saves one so the local container and Studio match.</summary>
    public string EnsureToken(string id)
    {
        string? existing = _store.Token(id);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        string token = ComputeProviderPresets.NewToken();
        _store.SetToken(id, token);
        Changed?.Invoke();
        return token;
    }

    public void SetDocker(string id, string? image, int port, bool useGpu, string containerName)
    {
        _store.SetDocker(id, image, port, useGpu, containerName);
        Changed?.Invoke();
    }

    public void SetStudioUrl(string id, string? url)
    {
        if (_store.SetStudioUrl(id, url)) Changed?.Invoke();
    }

    public string? RunpodApiKey(string id) => _store.RunpodApiKey(id);

    public void SetRunpod(
        string id,
        string? apiKey,
        string? image,
        string? gpuType,
        int? diskGb)
    {
        if (_store.SetRunpod(id, apiKey, image, gpuType, diskGb)) Changed?.Invoke();
    }

    public void SetRunpodPod(string id, string? podId, string? status)
    {
        if (_store.SetRunpodPod(id, podId, status)) Changed?.Invoke();
    }

    public void SetPrefab(string id, string? prefab)
    {
        if (_store.SetPrefab(id, prefab)) Changed?.Invoke();
    }

    /// <summary>Copies a host's settings (including secrets) under a new id.</summary>
    public ComputeProviderSettings Duplicate(string id)
    {
        ComputeProviderSettings source = _store.Find(id)
            ?? throw new InvalidOperationException("The Compute host was not found.");

        ComputeProviderSettings copy = _store.Add(
            $"{source.Name} copy",
            source.NormalizedKind,
            source.BaseUrl,
            _store.Token(id),
            source.Prefab);
        _store.SetDocker(
            copy.Id,
            source.DockerImage,
            source.DockerPort ?? ComputeProviderPresets.DefaultPort,
            source.UseGpu,
            ComputeProviderPresets.ContainerName(copy.Id));
        _store.SetStudioUrl(copy.Id, source.StudioUrl);
        _store.SetRunpod(
            copy.Id,
            _store.RunpodApiKey(id),
            source.RunpodImage,
            source.RunpodGpuType,
            source.RunpodDiskGb);
        Changed?.Invoke();
        return copy;
    }

    /// <summary>Cheap reachability check used by the provider dot and the Test button.</summary>
    public async Task<ComputeHealth> TestAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProvider provider = ResolveOrThrow(id);
        try
        {
            ComputeHealth health = await _client.GetHealthAsync(provider, cancellationToken);
            string status = health.ModelConfigured
                ? $"Online. Model {health.Model} {(health.ModelLoaded ? "loaded" : "ready")}."
                : "Online. No model uploaded yet.";
            _store.RecordProbe(id, true, status);
            IReadOnlyList<string>? models = await TryModelIdsAsync(provider, cancellationToken);
            if (models is { Count: > 0 })
            {
                _store.ReplaceModels(
                    id,
                    models,
                    health.ModelConfigured ? health.Model : null);
            }
            Changed?.Invoke();
            return health;
        }
        catch (Exception ex)
        {
            _store.RecordProbe(id, false, ex.Message);
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>Reads the host's model folder and remembers it for the chooser.</summary>
    public async Task<IReadOnlyList<ComputeModelInfo>> RefreshModelsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProvider provider = ResolveOrThrow(id);
        IReadOnlyList<ComputeModelInfo> models =
            await _client.ListModelsAsync(provider, cancellationToken);
        ComputeModelInfo? active = models.FirstOrDefault(model => model.Active);
        _store.ReplaceModels(
            id,
            models.Select(model => model.Id),
            active?.Id);
        _store.RecordProbe(id, true, $"{models.Count} model(s) on host.");
        Changed?.Invoke();
        return models;
    }

    /// <summary>Copies a local .smm/.gguf to the host and makes it active.</summary>
    public async Task<ComputeUploadResult> UploadModelAsync(
        string id,
        string localPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ComputeProvider provider = ResolveOrThrow(id);
        ComputeUploadResult result = await _client.UploadModelAsync(
            provider, localPath, activate: true, progress, cancellationToken);

        _store.RememberModel(id, result.Id);
        _store.RecordProbe(id, true, $"Model {result.Name} uploaded.");
        Changed?.Invoke();
        return result;
    }

    /// <summary>Deletes a model file from the host and forgets it locally.</summary>
    public async Task DeleteModelAsync(
        string id,
        string model,
        CancellationToken cancellationToken = default)
    {
        ComputeProvider provider = ResolveOrThrow(id);
        await _client.DeleteModelAsync(provider, model, cancellationToken);
        _store.ForgetModel(id, model);
        Changed?.Invoke();
    }

    /// <summary>Copies a dataset/job input to the host and returns its container path.</summary>
    public async Task<ComputeUploadResult> UploadFileAsync(
        string id,
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ComputeProvider provider = ResolveOrThrow(id);
        return await _client.UploadFileAsync(
            provider, localPath, remotePath, progress, cancellationToken);
    }

    public Task<ComputeHealth> GetHealthAsync(
        ComputeProvider provider,
        CancellationToken cancellationToken = default)
        => _client.GetHealthAsync(provider, cancellationToken);

    /// <summary>The host's loaded accelerator names, for the training device picker.</summary>
    public async Task<IReadOnlyList<ComputeAcceleratorInfo>> GetAcceleratorsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeHealth health = await _client
            .GetHealthAsync(ResolveOrThrow(id), cancellationToken);
        return health.Accelerators;
    }

    public ComputeClient Client => _client;

    private ComputeProvider ResolveOrThrow(string id)
        => _store.Resolve(id)
           ?? throw new InvalidOperationException("The Compute host was not found.");

    private async Task<IReadOnlyList<string>?> TryModelIdsAsync(
        ComputeProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ComputeModelInfo> models =
                await _client.ListModelsAsync(provider, cancellationToken);
            return [.. models.Select(model => model.Id)];
        }
        catch
        {
            return null;
        }
    }
}
