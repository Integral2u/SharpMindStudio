using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

public sealed record CanaryItem(string ModelPath, string Status, CanaryResult? Result);

public sealed class StudioCanaryService
{
    private readonly StudioActivityService _activity;
    private readonly object _gate = new();
    private List<CanaryItem> _items = [];
    private List<string> _modelPaths = [];
    private CanaryRequest? _request;
    private RunStore? _store;
    private CancellationTokenSource? _cts;
    private string _status = "Ready.";
    private string? _error;
    private bool _running;
    private bool _interrupted;
    private string? _currentModel;
    private Task? _current;

    public StudioCanaryService(StudioActivityService activity)
    {
        _activity = activity;
    }

    public event Action? Changed;

    public bool IsRunning => _running;
    public bool IsInterrupted => _interrupted;
    public bool CanContinue
    {
        get
        {
            lock (_gate)
                return !_running && _request is not null && _items.Any(item => item.Result is null);
        }
    }

    public IReadOnlyList<CanaryItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public IReadOnlyList<string> ModelPaths
    {
        get { lock (_gate) return [.. _modelPaths]; }
    }

    public IReadOnlyList<CanaryResult> Results
    {
        get { lock (_gate) return [.. _items.Where(item => item.Result is not null).Select(item => item.Result!)]; }
    }

    public string Status => _status;
    public string? Error => _error;
    public Task Current => _current ?? Task.CompletedTask;

    public Task StartAsync(CanaryRequest request, RunStore store)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        if (_running) return Current;

        lock (_gate)
        {
            _request = request;
            _store = store;
            _modelPaths = [.. request.Models];
            _items = [.. request.Models.Select(path => new CanaryItem(path, "queued", null))];
            _interrupted = false;
            _currentModel = null;
        }

        return BeginRunAsync(request, store);
    }

    public Task ContinueAsync()
    {
        if (_running) return Current;

        CanaryRequest request;
        RunStore store;
        lock (_gate)
        {
            if (_request is null || _store is null) return Task.CompletedTask;
            string[] remaining = [.. _items
                .Where(item => item.Result is null)
                .Select(item => item.ModelPath)];
            if (remaining.Length == 0) return Task.CompletedTask;

            _items = [.. _items.Select(item => item.Result is null
                ? item with { Status = "queued" }
                : item)];
            request = _request with { Models = remaining };
            store = _store;
            _interrupted = false;
            _currentModel = null;
        }

        return BeginRunAsync(request, store);
    }

    public void Interrupt()
    {
        if (!_running) return;
        _status = "Interrupting...";
        _cts?.Cancel();
        Notify();
    }

    private Task BeginRunAsync(CanaryRequest request, RunStore store)
    {
        _running = true;
        _error = null;
        _status = "Starting...";
        _cts = new CancellationTokenSource();
        _activity.Begin("Canary run starting...");
        Notify();
        _current = RunCoreAsync(request, store, _cts.Token);
        return _current;
    }

    private async Task RunCoreAsync(CanaryRequest request, RunStore store, CancellationToken cancellationToken)
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
                () => CanaryJob.RunAsync(
                    request, store, progress, MarkRunning, RecordResult, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (_interrupted)
            {
                _status = "Interrupted.";
            }
            else
            {
                _status = results.Any(result => !result.Succeeded)
                    ? "Canary finished with failures."
                    : "Canary finished.";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_currentModel is { } current)
                {
                    _items = [.. _items.Select(item =>
                        item.ModelPath.Equals(current, StringComparison.OrdinalIgnoreCase) &&
                        item.Result is null
                            ? item with { Status = "interrupted" }
                            : item)];
                }
            }

            _interrupted = true;
            _status = "Interrupted.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Canary failed.";
        }
        finally
        {
            _running = false;
            _currentModel = null;
            _current = null;
            _cts?.Dispose();
            _cts = null;
            _activity.End();
            Notify();
        }
    }

    private void MarkRunning(string model)
    {
        lock (_gate)
        {
            _currentModel = model;
            _items = [.. _items.Select(item =>
                item.ModelPath.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                item.Result is null
                    ? item with { Status = "running" }
                    : item)];
        }
        Notify();
    }

    private void RecordResult(CanaryResult result)
    {
        lock (_gate)
        {
            _items = [.. _items.Select(item =>
                item.ModelPath.Equals(result.ModelPath, StringComparison.OrdinalIgnoreCase)
                    ? item with { Status = result.Succeeded ? "done" : "failed", Result = result }
                    : item)];
        }
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
