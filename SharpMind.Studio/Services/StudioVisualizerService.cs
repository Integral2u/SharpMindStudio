using System.Diagnostics;
using SharpMind.Model.Analysis;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>
/// Captures a one-prompt activation map for the visualizer. It loads the model through the
/// host, runs a single forward with the activation hook, then releases the model; the page
/// keeps only the small layers x tokens map.
/// </summary>
public sealed class StudioVisualizerService : IAsyncDisposable
{
    private readonly InferenceSettingsService _inference;
    private readonly StudioModelHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private LoadedStudioModel? _loaded;
    private bool _disposed;

    public StudioVisualizerService(InferenceSettingsService inference, StudioModelHost host)
    {
        _inference = inference;
        _host = host;
    }

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Pick a model and capture a prompt.";
    public string? Error { get; private set; }
    public ActivationMap? Map { get; private set; }
    public string[] TokenText { get; private set; } = [];
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
            TokenText = [];
            ElapsedSeconds = 0;
            ModelName = descriptor.FileName;
            _cts = new CancellationTokenSource();
            Status = $"Loading {descriptor.FileName}...";
            Notify();

            ResolvedInference resolved = _inference.Resolve(
                descriptor.Family.Id, descriptor.Path, session: null);
            _loaded = await _host.LoadAsync(descriptor, resolved, progress: null, _cts.Token)
                .ConfigureAwait(false);

            Status = "Capturing activations...";
            Notify();
            int[] ids = _loaded.Tokenizer.Encode(prompt, addBos: true, addEos: false);
            var watch = Stopwatch.StartNew();
            ActivationMap map = await Task.Run(
                () => ActivationMapAnalyzer.Capture(_loaded.Model, ids, maxTokens, _cts.Token),
                _cts.Token).ConfigureAwait(false);
            watch.Stop();
            Map = map;
            TokenText = [.. ids.Take(map.Tokens).Select(id => _loaded.Tokenizer.Decode([id]) ?? "")];
            ElapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = $"Captured {map.Layers} layers x {map.Tokens} tokens in {ElapsedSeconds:F1}s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Activation capture cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Activation capture failed.";
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
