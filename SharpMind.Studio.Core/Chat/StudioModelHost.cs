using SharpMind.Core;
using SharpMind.Core.Quantization;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Inference;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Chat;

public sealed record ModelLoadProgress(string Message, double? Fraction);

public sealed class LoadedStudioModel : IDisposable
{
    public required string Key { get; init; }
    public required ModelDescriptor Descriptor { get; init; }
    public required Transformer Model { get; init; }
    public required TransformerWeights Weights { get; init; }
    public required Tokenizer Tokenizer { get; init; }
    public required ModelMetaData Meta { get; init; }
    public required ModelConfig Config { get; init; }

    /// <summary>The resolved kernel/architecture config the transformer was built with.</summary>
    public required SharpMindConfig SharpConfig { get; init; }

    internal int RefCount { get; set; }

    /// <summary>Last load/release; the idle TTL is measured from here.</summary>
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How long this model may stay cached with no users.</summary>
    public TimeSpan IdleTtl { get; set; } = StudioModelHost.DefaultIdleTtl;

    public void Dispose()
    {
        Model.Dispose();
        Weights.Dispose();
    }
}

public sealed class StudioModelHost : IDisposable
{
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LoadedStudioModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _trimTimer;

    public StudioModelHost()
    {
        _trimTimer = new Timer(
            _ => TrimExpired(),
            state: null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1));
    }

    public int LoadedModelCount
    {
        get
        {
            _gate.Wait();
            try { return _models.Count; }
            finally { _gate.Release(); }
        }
    }

    public async Task<LoadedStudioModel> LoadAsync(
        ModelDescriptor descriptor,
        ResolvedInference profile,
        IProgress<ModelLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string key = KeyFor(descriptor.Path, profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_models.TryGetValue(key, out LoadedStudioModel? cached) && cached is not null)
            {
                cached.RefCount++;
                cached.LastUsedUtc = DateTimeOffset.UtcNow;
                progress?.Report(new ModelLoadProgress($"Using loaded {descriptor.FileName}", 0.9));
                return cached;
            }

            foreach ((string idleKey, LoadedStudioModel idle) in _models
                .Where(pair => pair.Value.RefCount == 0)
                .ToArray())
            {
                _models.Remove(idleKey);
                idle.Dispose();
            }

            progress?.Report(new ModelLoadProgress($"Reading {descriptor.FileName}", 0));
            LoadedStudioModel loaded = await Task.Run(
                () => Load(descriptor, profile, key, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            loaded.RefCount = 1;
            loaded.LastUsedUtc = DateTimeOffset.UtcNow;
            _models[key] = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Release(LoadedStudioModel loaded, TimeSpan? idleTtl = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        _gate.Wait();
        try
        {
            if (!_models.TryGetValue(loaded.Key, out LoadedStudioModel? cached) ||
                !ReferenceEquals(cached, loaded))
                return;

            loaded.LastUsedUtc = DateTimeOffset.UtcNow;
            if (idleTtl is { } ttl) loaded.IdleTtl = ttl;
            loaded.RefCount--;
            if (loaded.RefCount > 0) return;

            // Keep idle models for their TTL; the timer trims them. A new load
            // still evicts all idle entries when it needs the space.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string idleKey, LoadedStudioModel idle) in _models
                .Where(pair => pair.Key != loaded.Key &&
                               pair.Value.RefCount == 0 &&
                               IsExpired(pair.Value, now))
                .ToArray())
            {
                _models.Remove(idleKey);
                idle.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsExpired(LoadedStudioModel model, DateTimeOffset now) =>
        model.LastUsedUtc + model.IdleTtl <= now;

    private void TrimExpired()
    {
        try
        {
            _gate.Wait();
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach ((string key, LoadedStudioModel idle) in _models
                    .Where(pair => pair.Value.RefCount == 0 && IsExpired(pair.Value, now))
                    .ToArray())
                {
                    _models.Remove(key);
                    idle.Dispose();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // The timer must never take the host down.
        }
    }

    public void Unload()
    {
        _gate.Wait();
        try
        {
            foreach (LoadedStudioModel model in _models.Values)
                model.Dispose();
            _models.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _trimTimer.Dispose();
        Unload();
        _gate.Dispose();
    }

    private static LoadedStudioModel Load(
        ModelDescriptor descriptor,
        ResolvedInference profile,
        string key,
        IProgress<ModelLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = descriptor.Path;
        ModelFormat format = path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)
            ? ModelFormat.Smm
            : ModelFormat.Gguf;
        var helper = ModelFormatHelpers.GetModelMetaHelperFor(format);
        helper.Load(path, null, out ModelMetaData meta, out ModelConfig config, out Tokenizer? tokenizer);
        if (tokenizer is null)
            throw new NotSupportedException("The model has no embedded tokenizer; Chat needs one.");

        LoadMode loadMode = profile.LoadMode.Equals("Streaming", StringComparison.OrdinalIgnoreCase)
            ? LoadMode.Streaming
            : LoadMode.Full;
        HardwareTier hardware = Enum.TryParse(profile.HardwareTier, ignoreCase: true, out HardwareTier tier)
            ? tier
            : HardwareTier.Auto;
        SharpMindConfig sharpConfig = config.ForModel(hardware) with
        {
            Parallel = profile.UseParallelKernels,
        };
        var mapping = sharpConfig.ToJigSawMapping(null, null);

        progress?.Report(new ModelLoadProgress($"Loading weights ({loadMode})", 0.1));
        TransformerWeights? weights = null;
        try
        {
            weights = ModelFactory.CreateWeights(
                config, sharpConfig, QuantizationFactory.Create(mapping), path,
                loadMode,
                quantizedResident: loadMode == LoadMode.Full,
                maxParallelLoadDegree: Math.Max(0, profile.MaxParallelLoadDegree));
            var weightProgress = progress is null
                ? null
                : new Progress<float>(value => progress.Report(
                    new ModelLoadProgress($"Loading weights {value:P0}", 0.1 + value * 0.75)));
            weights.InitializeWeights(weightProgress, cancellationToken);
            progress?.Report(new ModelLoadProgress("Building transformer", 0.9));
            Transformer model = ModelFactory.CreateTransformer(weights, sharpConfig, mapping);
            progress?.Report(new ModelLoadProgress("Model ready", 1));
            return new LoadedStudioModel
            {
                Key = key,
                Descriptor = descriptor,
                Model = model,
                Weights = weights,
                Tokenizer = tokenizer,
                Meta = meta,
                Config = config,
                SharpConfig = sharpConfig,
            };
        }
        catch
        {
            weights?.Dispose();
            throw;
        }
    }

    private static string KeyFor(string path, ResolvedInference profile) =>
        $"{Path.GetFullPath(path)}|{profile.LoadMode}|{profile.HardwareTier}|{profile.UseParallelKernels}|{profile.MaxParallelLoadDegree}";
}












