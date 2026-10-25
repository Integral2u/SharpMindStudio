using SharpMind.Core;
using SharpMind.Core.Quantization;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Inference;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Chat;

public sealed class LoadedStudioModel : IDisposable
{
    public required string Key { get; init; }
    public required ModelDescriptor Descriptor { get; init; }
    public required Transformer Model { get; init; }
    public required TransformerWeights Weights { get; init; }
    public required Tokenizer Tokenizer { get; init; }
    public required ModelMetaData Meta { get; init; }
    public required ModelConfig Config { get; init; }

    public void Dispose()
    {
        Model.Dispose();
        Weights.Dispose();
    }
}

public sealed class StudioModelHost : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoadedStudioModel? _current;

    public LoadedStudioModel? Current => _current;

    public async Task<LoadedStudioModel> LoadAsync(
        ModelDescriptor descriptor,
        ResolvedInference profile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string key = KeyFor(descriptor.Path, profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is not null && _current.Key == key)
                return _current;

            if (_current is not null)
            {
                _current.Dispose();
                _current = null;
            }

            progress?.Report($"Opening {descriptor.FileName}");
            LoadedStudioModel loaded = await Task.Run(
                () => Load(descriptor, profile, key, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            _current = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Unload()
    {
        _current?.Dispose();
        _current = null;
    }

    public void Dispose()
    {
        Unload();
        _gate.Dispose();
    }

    private static LoadedStudioModel Load(
        ModelDescriptor descriptor,
        ResolvedInference profile,
        string key,
        IProgress<string>? progress,
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

        progress?.Report($"Loading {descriptor.FileName} ({loadMode})");
        TransformerWeights? weights = null;
        try
        {
            weights = ModelFactory.CreateWeights(
                config, sharpConfig, QuantizationFactory.Create(mapping), path,
                loadMode,
                quantizedResident: loadMode == LoadMode.Full,
                maxParallelLoadDegree: Math.Max(0, profile.MaxParallelLoadDegree));
            weights.InitializeWeights();
            progress?.Report($"Building {descriptor.FileName}");
            Transformer model = ModelFactory.CreateTransformer(weights, sharpConfig, mapping);
            return new LoadedStudioModel
            {
                Key = key,
                Descriptor = descriptor,
                Model = model,
                Weights = weights,
                Tokenizer = tokenizer,
                Meta = meta,
                Config = config,
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
