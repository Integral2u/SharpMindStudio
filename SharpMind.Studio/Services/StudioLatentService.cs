using System.Diagnostics;
using SharpMind.Model.Analysis;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>
/// Projects a prompt's final hidden states into two dimensions for the latent-space lab.
/// The model is loaded through the host only for the forward pass and then released.
/// </summary>
public sealed class StudioLatentService : IAsyncDisposable
{
    private readonly InferenceSettingsService _inference;
    private readonly StudioModelHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private LoadedStudioModel? _loaded;
    private bool _disposed;

    public StudioLatentService(InferenceSettingsService inference, StudioModelHost host)
    {
        _inference = inference;
        _host = host;
    }

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Pick a model and project a prompt.";
    public string? Error { get; private set; }
    public LatentSpaceMap? Map { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public string? ModelName { get; private set; }

    public async Task RunAsync(ModelDescriptor descriptor, string prompt, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            IsRunning = true;
            Error = null;
            Map = null;
            ElapsedSeconds = 0;
            ModelName = descriptor.FileName;
            _cts = new CancellationTokenSource();
            Status = $"Loading {descriptor.FileName}...";
            Notify();

            ResolvedInference resolved = _inference.Resolve(
                descriptor.Family.Id, descriptor.Path, session: null);
            _loaded = await _host.LoadAsync(descriptor, resolved, progress: null, _cts.Token)
                .ConfigureAwait(false);

            Status = "Projecting hidden states...";
            Notify();
            int[] ids = _loaded.Tokenizer.Encode(prompt, addBos: true, addEos: false);
            var watch = Stopwatch.StartNew();
            LatentSpaceMap map = await Task.Run(
                () => LatentSpaceAnalyzer.Capture(
                    _loaded.Model, ids, id => _loaded.Tokenizer.Decode([id]) ?? "", maxTokens, _cts.Token),
                _cts.Token).ConfigureAwait(false);
            watch.Stop();
            Map = map;
            ElapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = $"Projected {map.Tokens} tokens in {ElapsedSeconds:F1}s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Latent projection cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Latent projection failed.";
        }
        finally
        {
            if (_loaded is not null)
            {
                _host.Release(_loaded);
                _loaded = null;
            }
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _gate.Release();
            Notify();
        }
    }

    public void Cancel() => _cts?.Cancel();

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_loaded is not null)
        {
            _host.Release(_loaded);
            _loaded = null;
        }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
