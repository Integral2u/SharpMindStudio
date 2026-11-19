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
public sealed class StudioSafetyService
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private SafetyReviewRequest? _pending;
    private SafetySettingsDocument _settings;

    public StudioSafetyService()
    {
        StudioPaths.EnsureCreated();
        _settings = Load();
    }

    public event Action? Changed;

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
