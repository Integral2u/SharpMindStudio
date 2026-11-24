using SharpMind.Studio.Core.Safety;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public enum SafetyDecision
{
    Deny,
    DenyAlways,
    AllowOnce,
    AllowAlways,
}

/// <summary>A blocked tool call waiting for the user's verdict.</summary>
public sealed class SafetyReviewRequest
{
    private readonly TaskCompletionSource<SafetyDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Tool { get; init; }
    public required SafetyFinding Finding { get; init; }

    public Task<SafetyDecision> Result => _completion.Task;

    public void Resolve(SafetyDecision decision) => _completion.TrySetResult(decision);

    public void Cancel() => _completion.TrySetCanceled();
}

public sealed record SafetySettingsDocument
{
    public const string CurrentSchema = "studio-safety/1";

    public string Schema { get; init; } = CurrentSchema;
    public bool Enabled { get; set; } = true;
    public string Threshold { get; set; } = nameof(SafetySeverity.High);
    public List<string> AllowedHosts { get; set; } = [];
}

/// <summary>
/// The user's final say over a blocked tool call. Deterministic rules decide
/// what is suspicious; the person decides what happens, and a decision can be
/// remembered for the session so the same question is not asked twice.
/// </summary>
public sealed class StudioSafetyService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deniedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedTools = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeTool;
    private readonly ProcessStartProbe _probe = new();
    private SafetyReviewRequest? _pending;
    private ProcessStartAlert? _pendingProcess;
    private SafetySettingsDocument _settings;

    public StudioSafetyService()
    {
        StudioPaths.EnsureCreated();
        _settings = Load();
        _probe.Started += OnProcessStarted;
    }

    public event Action? Changed;

    public ProcessStartAlert? PendingProcess
    {
        get { lock (_gate) return _pendingProcess; }
    }

    /// <summary>Watches for processes started under Studio while tools run.</summary>
    public void BeginToolWatch()
    {
        lock (_gate) _activeTool = null;
        _probe.Start();
    }

    public void EndToolWatch()
    {
        _probe.Stop();
        _probe.SetPlugin(null);
        lock (_gate) _activeTool = null;
    }

    public void SetActivePlugin(string? plugin) => _probe.SetPlugin(plugin);

    /// <summary>The tool whose permission check just ran; used to group process warnings.</summary>
    public void SetActiveTool(string? tool)
    {
        lock (_gate) _activeTool = string.IsNullOrWhiteSpace(tool) ? null : tool;
    }

    public bool KillPendingProcess(out string? error)
    {
        ProcessStartAlert? alert;
        lock (_gate)
        {
            alert = _pendingProcess;
            _pendingProcess = null;
        }
        Changed?.Invoke();

        if (alert is null)
        {
            error = null;
            return false;
        }
        return ProcessStartProbe.TryKill(alert.ProcessId, out error);
    }

    public void DismissPendingProcess()
    {
        lock (_gate) _pendingProcess = null;
        Changed?.Invoke();
    }

    /// <summary>Kill the process and suppress further warnings for that name this session.</summary>
    public (bool Ok, string? Error) DisablePendingProcess()
    {
        ProcessStartAlert? alert;
        lock (_gate)
        {
            alert = _pendingProcess;
            _pendingProcess = null;
            if (alert is not null) _deniedProcesses.Add(alert.Name);
        }

        bool ok = true;
        string? error = null;
        if (alert is not null)
            ok = ProcessStartProbe.TryKill(alert.ProcessId, out error);
        Changed?.Invoke();
        return (ok, error);
    }

    /// <summary>Stop suppressing this process/tool and ask again next time.</summary>
    public void AskAboutProcessAgain(ProcessStartAlert alert)
    {
        lock (_gate)
        {
            _allowedProcesses.Remove(alert.Name);
            _deniedProcesses.Remove(alert.Name);
            if (!string.IsNullOrWhiteSpace(alert.Tool)) _allowedTools.Remove(alert.Tool);
        }
        DismissPendingProcess();
    }

    /// <summary>
    /// Allow the tool that started this process for the rest of the app session.
    /// Falls back to the process name when the originating tool is unknown.
    /// </summary>
    public void AllowProcessForSession(ProcessStartAlert alert)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(alert.Tool))
            {
                _allowedTools.Add(alert.Tool);
            }
            else if (!string.IsNullOrWhiteSpace(alert.Name))
            {
                _allowedProcesses.Add(alert.Name);
                _deniedProcesses.Remove(alert.Name);
            }

            if (_pendingProcess is { } pending && pending.ProcessId == alert.ProcessId)
                _pendingProcess = null;
        }
        Changed?.Invoke();
    }

    private void OnProcessStarted(ProcessStartAlert alert)
    {
        string? tool;
        lock (_gate)
        {
            if (_allowedProcesses.Contains(alert.Name) ||
                _deniedProcesses.Contains(alert.Name))
            {
                return;
            }

            tool = _activeTool;
            if (!string.IsNullOrWhiteSpace(tool) && _allowedTools.Contains(tool))
                return;
        }

        if (!ProcessStartProbe.IsAlive(alert.ProcessId)) return;
        alert = alert with { Tool = tool };

        lock (_gate) _pendingProcess = alert;
        Changed?.Invoke();
        _ = DismissWhenExitedAsync(alert);
    }

    private async Task DismissWhenExitedAsync(ProcessStartAlert alert)
    {
        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);

            lock (_gate)
            {
                if (!ReferenceEquals(_pendingProcess, alert)) return;
            }

            if (ProcessStartProbe.IsAlive(alert.ProcessId)) continue;

            lock (_gate)
            {
                if (ReferenceEquals(_pendingProcess, alert)) _pendingProcess = null;
            }
            Changed?.Invoke();
            return;
        }
    }

    public void Dispose() => _probe.Dispose();

    public bool Enabled => _settings.Enabled;

    public SafetySeverity Threshold => Enum.TryParse(
        _settings.Threshold, ignoreCase: true, out SafetySeverity value)
        ? value
        : SafetySeverity.High;

    public IReadOnlyCollection<string> AllowedHosts => _settings.AllowedHosts;

    public SafetyReviewRequest? Pending
    {
        get { lock (_gate) return _pending; }
    }

    public bool IsDenied(string tool, string rule)
    {
        lock (_gate) return _denied.Contains(Key(tool, rule));
    }

    public bool IsAllowed(string tool, string rule)
    {
        lock (_gate) return _allowed.Contains(Key(tool, rule));
    }

    /// <summary>
    /// Asks the user about one finding. Cancelling the turn cancels the
    /// question, and a remembered answer skips it next time.
    /// </summary>
    public async Task<SafetyDecision> ReviewAsync(
        string tool,
        SafetyFinding finding,
        CancellationToken cancellationToken = default)
    {
        string key = Key(tool, finding.Rule);
        lock (_gate)
        {
            if (_denied.Contains(key)) return SafetyDecision.DenyAlways;
            if (_allowed.Contains(key)) return SafetyDecision.AllowAlways;
        }

        var request = new SafetyReviewRequest { Tool = tool, Finding = finding };
        lock (_gate)
        {
            if (_pending is not null)
            {
                // Only one question at a time; the second call waits its turn.
                return SafetyDecision.Deny;
            }
            _pending = request;
        }
        Changed?.Invoke();

        SafetyDecision decision;
        try
        {
            await using CancellationTokenRegistration registration =
                cancellationToken.Register(request.Cancel);
            decision = await request.Result.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Clear(request);
            throw;
        }

        Clear(request);
        lock (_gate)
        {
            if (decision == SafetyDecision.DenyAlways) _denied.Add(key);
            if (decision == SafetyDecision.AllowAlways) _allowed.Add(key);
        }
        return decision;
    }

    public Task ResolveAsync(SafetyDecision decision)
    {
        SafetyReviewRequest? request;
        lock (_gate)
        {
            request = _pending;
            _pending = null;
        }
        request?.Resolve(decision);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void CancelPending()
    {
        SafetyReviewRequest? request;
        lock (_gate)
        {
            request = _pending;
            _pending = null;
        }
        request?.Cancel();
        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        _settings = _settings with { Enabled = enabled };
        Save();
        Changed?.Invoke();
    }

    public void SetThreshold(SafetySeverity threshold)
    {
        _settings = _settings with { Threshold = threshold.ToString() };
        Save();
        Changed?.Invoke();
    }

    public void SetAllowedHosts(IEnumerable<string> hosts)
    {
        _settings = _settings with
        {
            AllowedHosts = [.. hosts
                .Select(host => host.Trim().ToLowerInvariant())
                .Where(host => host.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        Save();
        Changed?.Invoke();
    }

    /// <summary>Drops remembered Deny always / Allow always answers.</summary>
    public void ClearRemembered()
    {
        lock (_gate)
        {
            _denied.Clear();
            _allowed.Clear();
            _deniedProcesses.Clear();
            _allowedProcesses.Clear();
            _allowedTools.Clear();
        }
        Changed?.Invoke();
    }

    private void Clear(SafetyReviewRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, request)) _pending = null;
        }
        Changed?.Invoke();
    }

    private static string Key(string tool, string rule) => $"{tool}|{rule}";

    private static string Path => System.IO.Path.Combine(StudioPaths.Projects, "safety.json");

    private static SafetySettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new SafetySettingsDocument();
            SafetySettingsDocument document = JsonFile.Read<SafetySettingsDocument>(Path);
            if (document.Schema != SafetySettingsDocument.CurrentSchema)
                return new SafetySettingsDocument();
            document.AllowedHosts ??= [];
            return document;
        }
        catch
        {
            return new SafetySettingsDocument();
        }
    }

    private void Save()
    {
        try
        {
            JsonFile.Write(Path, _settings);
        }
        catch
        {
            // A read-only settings folder must not stop a chat.
        }
    }
}
