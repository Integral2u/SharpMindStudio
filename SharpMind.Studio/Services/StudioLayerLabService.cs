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
    public string? ModelPath { get; private set; }

    public bool IsExporting { get; private set; }
    public string? ExportStatus { get; private set; }
    public string? ExportError { get; private set; }
    public SurgeryExportResult? ExportResult { get; private set; }

    public async Task RunAsync(
        ModelDescriptor descriptor,
        string prompt,
        LayerAblation mode,
        int maxTokens,
        int maxSkipLayers = 8)
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
            ModelPath = descriptor.Path;
            ExportResult = null;
            ExportError = null;
            ExportStatus = null;
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
                ids, mode, maxTokens: maxTokens, maxSkipLayers: maxSkipLayers,
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

    /// <summary>
    /// Writes a pruned GGUF with the given blocks removed. The exporter only opens the source
    /// for reading, so it can run without a loaded model; the gate keeps it from overlapping
    /// with an ablation run.
    /// </summary>
    public async Task ExportPrunedAsync(
        IReadOnlyList<int> layers,
        string? destinationPath = null)
    {
        if (layers.Count == 0) return;
        string? sourcePath = ModelPath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            ExportError = "Run the lab on a model before exporting.";
            Notify();
            return;
        }
        if (!sourcePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            ExportError = "The pruned export only supports GGUF models.";
            Notify();
            return;
        }

        string destination = destinationPath ?? DefaultExportPath(sourcePath, layers);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning || IsExporting) return;
            IsExporting = true;
            ExportResult = null;
            ExportError = null;
            ExportStatus = $"Exporting {layers.Count} removed layer(s)...";
            Notify();

            var remove = layers.ToHashSet();
            SurgeryExportResult result = await Task.Run(
                () => ModelSurgeryExporter.ExportPruned(
                    sourcePath, destination, remove,
                    progress: (done, total) =>
                    {
                        ExportStatus = $"Copying tensor {done}/{total}...";
                        Notify();
                    })).ConfigureAwait(false);

            ExportResult = result;
            ExportStatus =
                $"Wrote {result.KeptLayers} layers and {result.Tensors} tensors " +
                $"({result.Bytes / (1024.0 * 1024.0):F1} MB).";
        }
        catch (Exception ex)
        {
            ExportError = $"{ex.GetType().Name}: {ex.Message}";
            ExportStatus = "Pruned export failed.";
        }
        finally
        {
            IsExporting = false;
            _gate.Release();
            Notify();
        }
    }

    private static string DefaultExportPath(string sourcePath, IReadOnlyList<int> layers)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string suffix = string.Join("-", layers);
        return Path.Combine(directory, $"{name}-pruned-{suffix}.gguf");
    }

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
