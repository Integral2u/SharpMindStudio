using System.Diagnostics;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;

namespace SharpMind.Studio.Services;

/// <summary>
/// Owns the one training run Studio can execute at a time. The UI creates or
/// loads a <see cref="TrainingJobSettings"/>, calls <see cref="Start"/>, and
/// reads progress snapshots while <see cref="Changed"/> fires from the run
/// thread. All shared state is guarded so a Blazor circuit can poll it safely.
/// </summary>
public sealed class StudioTrainingService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<string> _log = [];
    private readonly List<TrainingCurvePoint> _curve = [];
    private IReadOnlyList<TrainingRunSummary>? _historyCache;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;

    private TrainingJobSettings? _job;
    private TrainingJobSettings? _draft;
    private TrainingRunResult? _result;
    private Task? _run;    private string _status = "No training run yet.";
    private string? _error;
    private string? _exportPath;
    private string? _adapterPath;
    private float _progress;
    private int _currentStep;
    private int _totalSteps;
    private double? _loss;
    private double? _valLoss;
    private double? _learningRate;
    private double? _gradNorm;
    private double? _tokensPerSecond;
    private double? _bestValLoss;
    private int? _bestValStep;
    private bool _running;

    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    public string? ExportPath { get { lock (_gate) return _exportPath; } }
    public string? AdapterPath { get { lock (_gate) return _adapterPath; } }
    public TrainingJobSettings? Job { get { lock (_gate) return _job; } }
    public TrainingRunResult? Result { get { lock (_gate) return _result; } }

    /// <summary>
    /// Unsaved New-model editor state, kept on the singleton so switching tabs
    /// does not throw away a half-configured job. In-memory only; Start/Save
    /// still write the `.smmt`.
    /// </summary>
    public TrainingJobSettings? Draft
    {
        get { lock (_gate) return _draft; }
        set { lock (_gate) _draft = value; }
    }

    /// <summary>Build-map planner selection, kept across tab switches.</summary>
    public PlannerStart PlannerStart { get; set; } = PlannerStart.Nothing;

    /// <summary>Plain-language intake behind the planner rules.</summary>
    public PlannerIntake Intake { get; set; } = new();

    /// <summary>Build-map goals; mutable so the panel can toggle them.</summary>
    public HashSet<PlannerGoal> PlannerGoals { get; } = [];
    public float Progress { get { lock (_gate) return _progress; } }
    public int CurrentStep { get { lock (_gate) return _currentStep; } }
    public int TotalSteps { get { lock (_gate) return _totalSteps; } }
    public double? Loss { get { lock (_gate) return _loss; } }
    public double? ValLoss { get { lock (_gate) return _valLoss; } }
    public double? LearningRate { get { lock (_gate) return _learningRate; } }
    public double? GradNorm { get { lock (_gate) return _gradNorm; } }
    public double? TokensPerSecond { get { lock (_gate) return _tokensPerSecond; } }
    public double? BestValLoss { get { lock (_gate) return _bestValLoss; } }
    public int? BestValStep { get { lock (_gate) return _bestValStep; } }
    public TimeSpan Elapsed { get { lock (_gate) return _clock.Elapsed; } }
    public IReadOnlyList<string> Log { get { lock (_gate) return [.. _log]; } }

    /// <summary>Logged steps in order, for the live loss curve.</summary>
    public IReadOnlyList<TrainingCurvePoint> Curve { get { lock (_gate) return [.. _curve]; } }

    public IReadOnlyList<string> SavedJobs
    {
        get
        {
            try { return TrainingJobSettings.ListSaved(); }
            catch { return []; }
        }
    }

    /// <summary>Persisted run history, newest first. Cached until a run finishes.</summary>
    public IReadOnlyList<TrainingRunSummary> RunHistory
    {
        get
        {
            lock (_gate)
            {
                if (_historyCache is null) _historyCache = TrainingRunStore.List();
                return _historyCache;
            }
        }
    }

    public void DeleteRun(string path)
    {
        TrainingRunStore.Delete(path);
        lock (_gate) _historyCache = null;
        Changed?.Invoke();
    }

    public void RefreshRunHistory()
    {
        lock (_gate) _historyCache = null;
        Changed?.Invoke();
    }

    public string Folder => TrainingJobSettings.DefaultFolder;

    /// <summary>Starts a run. Returns false when one is already running.</summary>
    public bool Start(TrainingJobSettings job, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_gate)
        {
            if (_running) return false;

            _job = job;
            _result = null;
            _error = null;
            _exportPath = null;
            _adapterPath = null;
            _progress = 0;
            _currentStep = 0;
            _totalSteps = Math.Max(1, job.TotalSteps);
            _loss = _valLoss = _learningRate = _gradNorm = _tokensPerSecond = null;
            _bestValLoss = null;
            _bestValStep = null;
            _status = "Preparing training…";
            _log.Clear();
            _curve.Clear();
            _running = true;
            _clock.Restart();
            _cts = new CancellationTokenSource();
        }

        SaveWorkingCopy(job);
        _run = Task.Run(() => RunCoreAsync(job, pluginsFolder, _cts.Token));
        Changed?.Invoke();
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
                _status = "Interrupting — saving a checkpoint, then stopping…";
            }
        }
        Changed?.Invoke();
    }

    public TrainingJobSettings? LoadJob(string path, out string? error)
        => TrainingJobSettings.Load(path, out error);

    public bool SaveJob(TrainingJobSettings job, out string? error)
    {
        string path = string.IsNullOrWhiteSpace(job.SourcePath)
            ? Path.Combine(TrainingJobSettings.DefaultFolder, Sanitize(job.Name) + TrainingJobSettings.JobExtension)
            : job.SourcePath;
        job.SourcePath = path;
        return job.Save(path, out error);
    }

    private async Task RunCoreAsync(TrainingJobSettings job, string pluginsFolder, CancellationToken cancellationToken)
    {
        TrainingRunResult result;
        try
        {
            result = await TrainingRunner.RunAsync(
                job,
                pluginsFolder,
                new Relay<string>(AddLog),
                new Relay<float>(value => SetProgress(value)),
                step => ReportStep(step),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new TrainingRunResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
            AddLog($"Training failed: {result.Error}");
        }

        TrainingRunRecord? record = null;
        lock (_gate)
        {
            _running = false;
            _result = result;
            _error = result.Success ? null : result.Error;
            _exportPath = result.Success ? result.ExportPath : null;
            _adapterPath = result.Success ? result.AdapterPath : null;
            _bestValLoss = result.BestValLoss ?? _bestValLoss;
            _bestValStep = result.BestValStep ?? _bestValStep;
            _status = result.Success
                ? result.NothingToTrain
                    ? "Nothing new to train."
                    : result.ExportPath is null && result.AdapterPath is not null
                        ? $"Finished at step {result.FinalStep}. Adapter saved."
                        : $"Finished at step {result.FinalStep}. Model saved."
                : string.Equals(result.Error, "cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Training interrupted. The latest checkpoint was kept."
                    : "Training failed.";
            _clock.Stop();
            _cts?.Dispose();
            _cts = null;

            if (_job is { } settings)
                record = TrainingRunRecord.From(settings, result, _curve, _clock.Elapsed,
                    result.Success ? result.ExportPath : null);
            _historyCache = null;
        }

        if (record is not null)
        {
            try { TrainingRunStore.Save(record); }
            catch
            {
                // History is a convenience; losing it must not change the run result.
            }
        }

        Changed?.Invoke();
    }

    private void ReportStep(TrainStepResult step)
    {
        lock (_gate)
        {
            _currentStep = step.Step;
            _totalSteps = Math.Max(_totalSteps, step.Step);
            _loss = step.Loss;
            _valLoss = step.ValLoss;
            _learningRate = step.LearningRate;
            _gradNorm = step.GradNorm;
            _tokensPerSecond = step.TokensPerSecond;

            // One point per logged step. A resumed run starts at its recorded
            // step, so keep the curve monotonic rather than appending an older
            // step after the head.
            if (_curve.Count == 0 || step.Step > _curve[^1].Step)
            {
                _curve.Add(new TrainingCurvePoint(
                    step.Step, step.Loss, step.LearningRate,
                    step.ValLoss is { } val && double.IsFinite(val) ? val : null));
            }

            if (step.ValLoss is { } valLoss && double.IsFinite(valLoss)
                && (_bestValLoss is null || valLoss < _bestValLoss))
            {
                _bestValLoss = valLoss;
                _bestValStep = step.Step;
            }
        }
        Changed?.Invoke();
    }

    private void SetProgress(float value)
    {
        lock (_gate) _progress = Math.Clamp(value, 0, 1);
        Changed?.Invoke();
    }

    private void AddLog(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            // A very chatty run should not grow the circuit's memory without bound.
            if (_log.Count > 2000) _log.RemoveRange(0, _log.Count - 2000);
            _status = line;
        }
        Changed?.Invoke();
    }

    private void SaveWorkingCopy(TrainingJobSettings job)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.SourcePath))
            {
                string path = Path.Combine(
                    TrainingJobSettings.DefaultFolder,
                    Sanitize(job.Name) + TrainingJobSettings.JobExtension);
                if (job.Save(path, out _)) job.SourcePath = path;
            }
            else
            {
                job.Save(job.SourcePath, out _);
            }
        }
        catch
        {
            // A failed save must not stop a run: the runner derives its own
            // tokenizer and checkpoint locations from the job.
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "training-job" : name.Trim();
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
