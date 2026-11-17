using Microsoft.Extensions.Hosting;
using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>One instance in the visible Keep countdown.</summary>
public sealed record ComputeIdleNotice(
    string ProviderId,
    string InstanceId,
    string Label,
    TimeSpan Remaining);

/// <summary>
/// Watches the instances this Studio created and starts a 10-minute Keep
/// countdown once a host has been idle for its configured window (15/30/60
/// minutes, or never). Untracked instances are ignored: a tagged instance from
/// another install is never destroyed here.
/// </summary>
public sealed class ComputeIdleService : IHostedService, IDisposable
{
    private readonly ComputeDashboardService _dashboard;
    private readonly ComputeProviderService _compute;
    private readonly StudioTrainingService _training;
    private readonly StudioRehearsalService _rehearsal;
    private readonly StudioExamService _exam;
    private readonly ComputeActivityService _activity;
    private readonly ComputeIdleTracker _tracker = new();
    private readonly Dictionary<string, JobProbe> _jobProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly HashSet<string> _destroying = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ComputeIdleNotice> _notices = [];
    private string? _error;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ComputeIdleService(
        ComputeDashboardService dashboard,
        ComputeProviderService compute,
        StudioTrainingService training,
        StudioRehearsalService rehearsal,
        StudioExamService exam,
        ComputeActivityService activity)
    {
        _dashboard = dashboard;
        _compute = compute;
        _training = training;
        _rehearsal = rehearsal;
        _exam = exam;
        _activity = activity;
    }

    public event Action? Changed;

    public IReadOnlyList<ComputeIdleNotice> Notices
    {
        get { lock (_gate) return _notices; }
    }

    public string? Error
    {
        get { lock (_gate) return _error; }
    }

    public static string Format(TimeSpan remaining)
    {
        int total = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{total / 60}:{total % 60:00}";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Resets the idle window for one host and clears its countdown.</summary>
    public void Keep(string providerId)
    {
        _tracker.Keep(providerId, DateTimeOffset.UtcNow);
        RemoveNotice(providerId);
        Notify();
    }

    /// <summary>Destroys one tracked instance immediately, with the same ownership guard.</summary>
    public async Task DestroyNowAsync(string providerId)
    {
        VultrComputeInstance? instance = _dashboard.Instances.FirstOrDefault(value =>
            string.Equals(value.OwnerProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (instance is null || !ComputeTeardown.IsOwned(instance)) return;

        await DestroyAsync(instance, CancellationToken.None).ConfigureAwait(false);
        RemoveNotice(providerId);
        Notify();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetError(ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await _dashboard.RefreshAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool training = _training.IsRunning || _rehearsal.IsRunning || _exam.IsRunning;
        string? trainingProvider = _training.RemoteProviderId;

        var notices = new List<ComputeIdleNotice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VultrComputeInstance instance in _dashboard.Instances)
        {
            // Only instances this Studio created can be torn down. Tagged
            // instances from another install or a deleted host are left alone.
            if (!ComputeTeardown.IsOwned(instance)) continue;

            string owner = instance.OwnerProviderId!;
            seen.Add(owner);
            if (!instance.IsRunning || IsDestroying(owner))
            {
                if (!instance.IsRunning) _tracker.Forget(owner);
                continue;
            }

            int minutes = _compute.Find(owner)?.IdleTeardownMinutes
                ?? ComputeIdleTracker.DefaultTimeoutMinutes;
            bool hostJob = await HasActiveJobAsync(owner, now, cancellationToken)
                .ConfigureAwait(false);
            bool chatBusy = _activity.IsChatActive(owner);
            bool inUse = hostJob || _activity.IsActive(owner);
            string? activity = hostJob
                ? "job"
                : ComputeUsage.Activity(instance, trainingProvider, training, owner, chatBusy);

            ComputeIdleObservation observation = _tracker.Observe(
                owner, minutes, inUse, now);
            if (observation.Phase == ComputeIdlePhase.Destroy)
            {
                await DestroyAsync(instance, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (observation.Phase == ComputeIdlePhase.Countdown)
            {
                notices.Add(new ComputeIdleNotice(
                    owner, instance.InstanceId, instance.Label, observation.Remaining));
            }
        }

        foreach (string provider in _tracker.Providers)
        {
            if (!seen.Contains(provider)) _tracker.Forget(provider);
        }
        foreach (string provider in _jobProbes.Keys)
        {
            if (!seen.Contains(provider)) _jobProbes.Remove(provider);
        }

        SetNotices(notices);
    }

    /// <summary>
    /// A remote job that outlived a Studio restart still counts as use. The host
    /// is probed at most every 30 seconds; an unreachable host is treated as busy
    /// so a running job is not torn down.
    /// </summary>
    private async Task<bool> HasActiveJobAsync(
        string providerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_jobProbes.TryGetValue(providerId, out JobProbe? probe)
            && probe is not null
            && now - probe.At < TimeSpan.FromSeconds(30))
        {
            return probe.Active;
        }

        ComputeProvider? provider = _compute.Resolve(providerId);
        bool active = false;
        if (provider is null || !provider.IsUsable)
        {
            active = true;
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                IReadOnlyList<ComputeJob> jobs = await _compute.Client
                    .ListJobsAsync(provider, timeout.Token)
                    .ConfigureAwait(false);
                active = jobs.Any(job =>
                    job.Status is "queued" or "running" or "cancelling");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                active = true;
            }
        }

        _jobProbes[providerId] = new JobProbe(now, active);
        return active;
    }

    private async Task DestroyAsync(
        VultrComputeInstance instance,
        CancellationToken cancellationToken)
    {
        string owner = instance.OwnerProviderId!;
        lock (_gate)
        {
            if (!_destroying.Add(owner)) return;
        }

        try
        {
            await _dashboard.DestroyAsync(instance, cancellationToken).ConfigureAwait(false);
            _tracker.Forget(owner);
            SetError(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError($"{instance.Label}: {ex.Message}");
        }
        finally
        {
            lock (_gate) _destroying.Remove(owner);
        }
    }

    private bool IsDestroying(string providerId)
    {
        lock (_gate) return _destroying.Contains(providerId);
    }

    private void RemoveNotice(string providerId)
    {
        lock (_gate)
        {
            _notices = [.. _notices.Where(notice =>
                !string.Equals(notice.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))];
        }
    }

    private void SetNotices(List<ComputeIdleNotice> notices)
    {
        bool changed;
        lock (_gate)
        {
            changed = _notices.Count != notices.Count
                || _notices.Zip(notices).Any(pair =>
                    !string.Equals(pair.First.ProviderId, pair.Second.ProviderId,
                        StringComparison.OrdinalIgnoreCase)
                    || Math.Abs((pair.First.Remaining - pair.Second.Remaining).TotalSeconds) >= 1);
            _notices = notices;
        }

        if (changed) Notify();
    }

    private void SetError(string? error)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_error, error, StringComparison.Ordinal);
            _error = error;
        }

        if (changed) Notify();
    }

    private void Notify() => Changed?.Invoke();

    private sealed record JobProbe(DateTimeOffset At, bool Active);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
