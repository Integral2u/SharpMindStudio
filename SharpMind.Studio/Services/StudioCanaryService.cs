using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

public sealed class StudioCanaryService
{
    private readonly StudioActivityService _activity;
    private readonly List<CanaryResult> _results = [];
    private readonly object _resultGate = new();
    private string _status = "Ready.";
    private string? _error;
    private bool _running;
    private Task? _current;

    public StudioCanaryService(StudioActivityService activity)
    {
        _activity = activity;
    }

    public event Action? Changed;

    public bool IsRunning => _running;
    public IReadOnlyList<CanaryResult> Results
    {
        get
        {
            lock (_resultGate) return [.. _results];
        }
    }
    public string Status => _status;
    public string? Error => _error;
    public Task Current => _current ?? Task.CompletedTask;

    public Task StartAsync(CanaryRequest request, RunStore store)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        if (_running) return Current;

        _running = true;
        _error = null;
        lock (_resultGate) _results.Clear();
        _status = "Starting...";
        _activity.Begin("Canary run starting...");
        Notify();

        _current = RunCoreAsync(request, store);
        return _current;
    }

    private async Task RunCoreAsync(CanaryRequest request, RunStore store)
    {
        var progress = new Progress<string>(message =>
        {
            _status = message;
            _activity.Report(message);
            Notify();
        });

        try
        {
            IReadOnlyList<CanaryResult> results = await Task.Run(
                () => CanaryJob.RunAsync(request, store, progress, RecordResult)).ConfigureAwait(false);
            lock (_resultGate)
            {
                _results.Clear();
                _results.AddRange(results);
            }
            _status = results.Any(result => !result.Succeeded)
                ? "Canary finished with failures."
                : "Canary finished.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Canary failed.";
        }
        finally
        {
            _running = false;
            _current = null;
            _activity.End();
            Notify();
        }
    }

    private void RecordResult(CanaryResult result)
    {
        lock (_resultGate) _results.Add(result);
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}


