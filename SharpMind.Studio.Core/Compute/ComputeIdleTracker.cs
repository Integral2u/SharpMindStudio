namespace SharpMind.Studio.Core.Compute;

public enum ComputeIdlePhase
{
    None,
    Idle,
    Countdown,
    Destroy,
}

public readonly record struct ComputeIdleObservation(ComputeIdlePhase Phase, TimeSpan Remaining);

/// <summary>
/// Tracks the idle clock for one instance per host. Training, chat and the other
/// uses are supplied by the caller; the tracker only decides when the countdown
/// starts and when it has expired.
/// </summary>
public sealed class ComputeIdleTracker
{
    public const int DefaultTimeoutMinutes = 30;
    public static readonly TimeSpan Countdown = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, IdleEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Providers => [.. _entries.Keys];

    public ComputeIdleObservation Observe(
        string providerId,
        int timeoutMinutes,
        bool inUse,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return default;

        // Never, or active work: there is nothing to count down.
        if (timeoutMinutes <= 0 || inUse)
        {
            _entries.Remove(providerId);
            return default;
        }

        if (_entries.TryGetValue(providerId, out IdleEntry? entry)
            && entry.CountdownUntil is { } until)
        {
            if (now >= until)
            {
                _entries.Remove(providerId);
                return new ComputeIdleObservation(ComputeIdlePhase.Destroy, TimeSpan.Zero);
            }

            return new ComputeIdleObservation(ComputeIdlePhase.Countdown, until - now);
        }

        if (entry is null)
        {
            entry = new IdleEntry(now);
            _entries[providerId] = entry;
        }

        TimeSpan timeout = TimeSpan.FromMinutes(timeoutMinutes);
        TimeSpan idleFor = now - entry.IdleSince;
        if (idleFor >= timeout)
        {
            entry.CountdownUntil = now + Countdown;
            return new ComputeIdleObservation(ComputeIdlePhase.Countdown, Countdown);
        }

        return new ComputeIdleObservation(ComputeIdlePhase.Idle, timeout - idleFor);
    }

    /// <summary>Keep clears the countdown and starts a fresh idle window.</summary>
    public void Keep(string providerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        _entries[providerId] = new IdleEntry(now);
    }

    public void Forget(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        _entries.Remove(providerId);
    }

    private sealed class IdleEntry(DateTimeOffset idleSince)
    {
        public DateTimeOffset IdleSince { get; } = idleSince;
        public DateTimeOffset? CountdownUntil { get; set; }
    }
}
