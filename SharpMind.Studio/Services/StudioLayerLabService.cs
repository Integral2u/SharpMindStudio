using System.Diagnostics;
using SharpMind.Model.Analysis;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs the model-agnostic layer-importance lab against a local model. The service owns
/// the analysis gate and reports progress through <see cref="Changed"/>; the page only
/// reads <see cref="Report"/> and <see cref="Status"/>.
/// </summary>
public sealed class StudioLayerLabService : IAsyncDisposable
{
    private readonly InferenceSettingsService _inference;
    private readonly StudioModelHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private LoadedStudioModel? _loaded;
    private bool _disposed;

    public StudioLayerLabService(InferenceSettingsService inference, StudioModelHost host)
    {
        _inference = inference;
        _host = host;
    }

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Pick a model and run the calibration.";
    public string? Error { get; private set; }
    public LayerImportanceReport? Report { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public string? ModelName { get; private set; }

    public async Task RunAsync(
        ModelDescriptor descriptor,
        string prompt,
        LayerAblation mode,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;

            IsRunning = true;
            Error = null;
            Report = null;
            ElapsedSeconds = 0;
            ModelName = descriptor.FileName;
            _cts = new CancellationTokenSource();
            Status = $"Loading {descriptor.FileName}...";
            Notify();

            ResolvedInference resolved = _inference.Resolve(
                descriptor.Family.Id, descriptor.Path, session: null);
            _loaded = await _host.LoadAsync(descriptor, resolved, progress: null, _cts.Token)
                .ConfigureAwait(false);

            Status = "Tokenizing the calibration text...";
            Notify();
            int[] ids = _loaded.Tokenizer.Encode(prompt, addBos: true, addEos: false);
            if (ids.Length < 2)
                throw new InvalidOperationException("The calibration text produced fewer than two tokens.");

            var analyzer = new LayerImportanceAnalyzer(_loaded.Model);
            var watch = Stopwatch.StartNew();
            Report = analyzer.Analyze(
                ids, mode, maxTokens: maxTokens,
                progress: (done, total) =>
                {
                    Status = $"Ablating layer {done}/{total}...";
                    Notify();
                },
                cancellationToken: _cts.Token);
            watch.Stop();
            ElapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = $"Done: {Report.Layers.Count} layers in {ElapsedSeconds:F0}s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Layer lab cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Layer lab failed.";
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
