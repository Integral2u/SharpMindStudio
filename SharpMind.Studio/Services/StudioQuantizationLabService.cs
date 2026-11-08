using System.Diagnostics;
using SharpMind.Model.Analysis;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs the tensor-level quantization comparison for the Labs page. It only opens the two
/// GGUF files and dequantizes one tensor at a time, so it does not load a model or compete
/// with the chat session for weights.
/// </summary>
public sealed class StudioQuantizationLabService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Pick a reference and a candidate quantization.";
    public string? Error { get; private set; }
    public QuantizationComparisonReport? Report { get; private set; }
    public double ElapsedSeconds { get; private set; }

    public async Task RunAsync(
        string referencePath,
        string candidatePath,
        string? nameFilter,
        int maxTensors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            IsRunning = true;
            Error = null;
            Report = null;
            ElapsedSeconds = 0;
            _cts = new CancellationTokenSource();
            Status = "Comparing tensors...";
            Notify();

            CancellationToken token = _cts.Token;
            var watch = Stopwatch.StartNew();
            Report = await Task.Run(
                () => QuantizationAnalyzer.Compare(
                    referencePath, candidatePath, nameFilter, maxTensors,
                    progress: (done, total) =>
                    {
                        Status = $"Tensor {done}/{total}...";
                        Notify();
                    },
                    cancellationToken: token),
                token).ConfigureAwait(false);
            watch.Stop();
            ElapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = $"Done: {Report.Compared} tensors in {ElapsedSeconds:F0}s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Quantization comparison cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Quantization comparison failed.";
        }
        finally
        {
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
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
