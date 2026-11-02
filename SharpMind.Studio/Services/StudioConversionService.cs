using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the one model conversion Studio can run at a time, so progress and the
/// result survive a tab switch or an enhanced-navigation navigation away and
/// back. Conversion itself lives in <see cref="ModelConverter"/>.
/// </summary>
public sealed class StudioConversionService : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;

    private bool _running;
    private float _progress;
    private string _status = "No conversion yet.";
    private string? _error;
    private ModelConversionResult? _result;

    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public float Progress { get { lock (_gate) return _progress; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    public ModelConversionResult? Result { get { lock (_gate) return _result; } }

    /// <summary>Starts a conversion. Returns false when one is already running.</summary>
    public bool Start(string sourcePath, string destinationPath, bool overwrite)
    {
        lock (_gate)
        {
            if (_running) return false;

            _running = true;
            _progress = 0;
            _error = null;
            _result = null;
            _status = "Reading the source model…";
            _cts = new CancellationTokenSource();
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(sourcePath, destinationPath, overwrite, _cts.Token));
        return true;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_running || _cts is null) return;
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _status = "Cancelling — cleaning up the partial file…";
            }
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_running) return;
            _error = null;
            _result = null;
            _progress = 0;
            _status = "No conversion yet.";
        }
        Changed?.Invoke();
    }

    private async Task RunAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        ModelConversionResult result;
        try
        {
            result = await ModelConverter.ConvertAsync(
                sourcePath,
                destinationPath,
                overwrite,
                new Relay<float>(ReportProgress),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ModelConversionResult
            {
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }

        lock (_gate)
        {
            _running = false;
            _result = result;
            _error = result.Success ? null : result.Error;
            _progress = result.Success ? 1 : _progress;
            _status = result.Success
                ? $"{result.Label} complete in {result.Elapsed.TotalSeconds:0.0}s."
                : result.Error?.StartsWith("Conversion cancelled", StringComparison.Ordinal) == true
                    ? "Conversion cancelled."
                    : "Conversion failed.";
            _cts?.Dispose();
            _cts = null;
        }

        Changed?.Invoke();
    }

    private void ReportProgress(float value)
    {
        lock (_gate)
        {
            _progress = Math.Clamp(value, 0, 1);
            _status = $"Converting… {(_progress * 100):0}%";
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
