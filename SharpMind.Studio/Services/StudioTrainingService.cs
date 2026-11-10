using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Serialization;
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
    private readonly ComputeProviderService _compute;
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
    private string? _remoteProviderId;
    private string? _remoteJobId;
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

    public StudioTrainingService(ComputeProviderService compute)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
    }

    /// <summary>The Compute host used by the current remote run, if any.</summary>
    public string? RemoteProviderId { get { lock (_gate) return _remoteProviderId; } }
    public bool IsRemoteRun => RemoteProviderId is not null;

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

    /// <summary>
    /// The architecture template last sent from the Model visualizer. The New
    /// model editor can re-apply it after other fields change.
    /// </summary>
    public ModelArchitectureTemplate? PendingTemplate { get; set; }

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

    /// <summary>User-saved pretrain and LoRA playbooks under Training/playbooks.</summary>
    public TrainingPlaybookStore Playbooks { get; } =
        new(Path.Combine(StudioPaths.Training, "playbooks"));

    /// <summary>Starts a run. Returns false when one is already running.</summary>
    public bool Start(TrainingJobSettings job, string pluginsFolder, string? computeProviderId = null)
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
            _remoteProviderId = computeProviderId;
            _remoteJobId = null;
            _status = computeProviderId is null
                ? "Preparing training…"
                : "Preparing remote training…";
            _log.Clear();
            _curve.Clear();
            _running = true;
            _clock.Restart();
            _cts = new CancellationTokenSource();
        }

        SaveWorkingCopy(job);
        _run = computeProviderId is null
            ? Task.Run(() => RunCoreAsync(job, pluginsFolder, _cts.Token))
            : Task.Run(() => RunRemoteCoreAsync(job, computeProviderId, _cts.Token));
        Changed?.Invoke();
        return true;
    }

    public void Cancel()
    {
        string? providerId = null;
        string? remoteId = null;
        lock (_gate)
        {
            if (!_running || _cts is null) return;
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _status = "Interrupting — saving a checkpoint, then stopping…";
                providerId = _remoteProviderId;
                remoteId = _remoteJobId;
            }
        }

        if (providerId is not null && remoteId is not null
            && _compute.Resolve(providerId) is { } provider)
        {
            _ = Task.Run(async () =>
            {
                try { await _compute.Client.CancelJobAsync(provider, remoteId); }
                catch { }
            });
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
                ? result.Cached
                    ? $"Recipe cache hit. Reused {Path.GetFileName(result.ExportPath)}."
                    : result.NothingToTrain
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

    /// <summary>
    /// Runs the job on a SharpMind Compute host. Local source files are uploaded
    /// first and their paths rewritten to the host's container paths; the export
    /// is downloaded back to the path the Training page already shows.
    /// </summary>
    private async Task RunRemoteCoreAsync(
        TrainingJobSettings job,
        string providerId,
        CancellationToken cancellationToken)
    {
        string? localExport = null;
        string? localAdapter = null;
        TrainingRunResult result;
        int lastStep = 0;

        try
        {
            ComputeProvider provider = _compute.Resolve(providerId)
                ?? throw new InvalidOperationException("The Compute host was not found.");
            if (!provider.IsUsable)
                throw new InvalidOperationException(
                    "The Compute host needs a URL and token. Add them in Settings → Providers.");

            AddLog($"Remote training on {provider.Name}.");
            string bucket = $"jobs/{Sanitize(job.Name)}";
            localExport = job.ExportFilePath;

            TrainingJobSettings remoteJob = CloneJob(job);
            remoteJob.SourcePath = null;
            remoteJob.IncrementalMode = false;
            remoteJob.SourceHashes = [];
            remoteJob.SourceFileHashes = [];
            remoteJob.ExportPath = $"{bucket}/out.smm";
            remoteJob.TokenizerCachePath = $"{bucket}/tokenizer.json";
            if (remoteJob.IsFineTune)
                remoteJob.AdapterPath = $"{bucket}/adapter.smlora";

            await UploadSourcesAsync(provider, remoteJob, bucket, cancellationToken)
                .ConfigureAwait(false);

            JsonObject payload = JsonSerializer.SerializeToNode(remoteJob, StudioJson.Event)
                as JsonObject
                ?? throw new InvalidOperationException("The training job could not be serialized.");

            ComputeJob created = await _compute.Client
                .StartTrainingAsync(provider, payload, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate) _remoteJobId = created.Id;
            AddLog($"Queued {created.Id} on {provider.Name}.");

            int lastLogCount = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                ComputeJob state = await _compute.Client
                    .GetJobAsync(provider, created.Id, cancellationToken)
                    .ConfigureAwait(false);

                lastStep = state.Step ?? lastStep;
                lock (_gate)
                {
                    _progress = (float)Math.Clamp(state.Progress, 0, 1);
                    if (state.Step is int step) _currentStep = step;
                    if (state.Loss is { } loss) _loss = loss;
                    if (state.ValLoss is { } valLoss) _valLoss = valLoss;
                    if (state.Step is int curveStep
                        && (_curve.Count == 0 || curveStep > _curve[^1].Step))
                    {
                        _curve.Add(new TrainingCurvePoint(
                            curveStep,
                            state.Loss ?? 0,
                            0,
                            state.ValLoss));
                    }
                    _status = state.Step is int current
                        ? $"Remote {state.Status} · step {current}"
                        : $"Remote {state.Status}…";
                }
                Changed?.Invoke();

                IReadOnlyList<string> logs = await _compute.Client
                    .GetJobLogAsync(provider, created.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (logs.Count > lastLogCount)
                {
                    for (int i = lastLogCount; i < logs.Count; i++)
                        AddLog(logs[i]);
                    lastLogCount = logs.Count;
                }

                if (string.Equals(state.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    if (localExport is not null)
                    {
                        AddLog("Downloading the trained model…");
                        await DownloadArtifactAsync(
                            provider, created.Id, localExport, artifact: null, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(state.AdapterPath) && localExport is not null)
                    {
                        localAdapter = job.AdapterFilePath;
                        try
                        {
                            await DownloadArtifactAsync(
                                provider, created.Id, localAdapter, "adapter", cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            localAdapter = null;
                        }
                    }

                    result = new TrainingRunResult
                    {
                        Success = true,
                        ExportPath = localExport,
                        AdapterPath = localAdapter,
                        FinalStep = lastStep,
                        BestValLoss = _bestValLoss,
                        BestValStep = _bestValStep,
                    };
                    break;
                }

                if (state.Status is "failed" or "cancelled")
                {
                    result = new TrainingRunResult
                    {
                        Success = false,
                        Error = state.Error ?? state.Status,
                        FinalStep = lastStep,
                    };
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new TrainingRunResult { Success = false, Error = "cancelled", FinalStep = lastStep };
        }
        catch (Exception ex)
        {
            result = new TrainingRunResult
            {
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                FinalStep = lastStep,
            };
            AddLog($"Remote training failed: {result.Error}");
        }

        TrainingRunRecord? record = null;
        lock (_gate)
        {
            _running = false;
            _result = result;
            _error = result.Success ? null : result.Error;
            _exportPath = result.Success ? result.ExportPath : null;
            _adapterPath = result.Success ? result.AdapterPath : null;
            _status = result.Success
                ? $"Finished on the remote host at step {result.FinalStep}. Model downloaded."
                : string.Equals(result.Error, "cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Remote training cancelled."
                    : "Remote training failed.";
            _clock.Stop();
            _cts?.Dispose();
            _cts = null;

            if (_job is { } settings)
            {
                record = TrainingRunRecord.From(
                    settings, result, _curve, _clock.Elapsed,
                    result.Success ? result.ExportPath : null);
            }
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

    private static TrainingJobSettings CloneJob(TrainingJobSettings job)
    {
        string json = JsonSerializer.Serialize(job, StudioJson.Event);
        return JsonSerializer.Deserialize<TrainingJobSettings>(json, StudioJson.Event)
            ?? throw new InvalidOperationException("The training job could not be cloned.");
    }

    /// <summary>Logs upload progress in 25% steps so a large corpus does not look stalled.</summary>
    private IProgress<double> UploadProgress(string name)
    {
        int next = 25;
        return new Progress<double>(value =>
        {
            int percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
            if (percent < next) return;
            AddLog($"Uploading {name}: {percent}%");
            next = percent + 25;
        });
    }

    private async Task UploadSourcesAsync(
        ComputeProvider provider,
        TrainingJobSettings job,
        string bucket,
        CancellationToken cancellationToken)
    {
        var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int files = 0;

        if (!string.IsNullOrWhiteSpace(job.BaseModelPath) && File.Exists(job.BaseModelPath))
        {
            AddLog($"Uploading base model {Path.GetFileName(job.BaseModelPath)}…");
            ComputeUploadResult result = await _compute
                .UploadFileAsync(
                    provider.Id,
                    job.BaseModelPath,
                    $"{bucket}/base/{Path.GetFileName(job.BaseModelPath)}",
                    UploadProgress(Path.GetFileName(job.BaseModelPath)),
                    cancellationToken)
                .ConfigureAwait(false);
            job.BaseModelPath = result.Path;
            files++;
        }

        if (!string.IsNullOrWhiteSpace(job.ResumeFrom))
        {
            if (File.Exists(job.ResumeFrom))
            {
                AddLog($"Uploading resume checkpoint {Path.GetFileName(job.ResumeFrom)}…");
                ComputeUploadResult result = await _compute
                    .UploadFileAsync(
                        provider.Id,
                        job.ResumeFrom,
                        $"{bucket}/resume/{Path.GetFileName(job.ResumeFrom)}",
                        UploadProgress(Path.GetFileName(job.ResumeFrom)),
                        cancellationToken)
                    .ConfigureAwait(false);
                job.ResumeFrom = result.Path;
                files++;
            }
            else if (Directory.Exists(job.ResumeFrom))
            {
                AddLog("Uploading resume checkpoints…");
                foreach (string file in Directory.EnumerateFiles(
                    job.ResumeFrom, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(job.ResumeFrom, file).Replace('\\', '/');
                    await _compute
                        .UploadFileAsync(
                            provider.Id, file, $"{bucket}/resume/{relative}",
                            UploadProgress(Path.GetFileName(file)), cancellationToken)
                        .ConfigureAwait(false);
                    files++;
                }
                job.ResumeFrom = $"{bucket}/resume";
            }
        }

        for (int index = 0; index < job.Sources.Count; index++)
        {
            JobComponent component = job.Sources[index].Component;
            foreach (string key in component.Args.Keys.ToList())
            {
                string value = component.Args[key];
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (File.Exists(value))
                {
                    if (!uploaded.TryGetValue(value, out string? remote))
                    {
                        remote = $"{bucket}/inputs/s{index}/{Path.GetFileName(value)}";
                        AddLog($"Uploading {Path.GetFileName(value)}…");
                        ComputeUploadResult result = await _compute
                            .UploadFileAsync(provider.Id, value, remote,
                            UploadProgress(Path.GetFileName(value)), cancellationToken)
                            .ConfigureAwait(false);
                        uploaded[value] = result.Path;
                        remote = result.Path;
                        files++;
                    }
                    component.Args[key] = remote!;
                }
                else if (Directory.Exists(value))
                {
                    string folder = Path.GetFileName(
                        value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string remoteRoot = $"{bucket}/inputs/s{index}/{folder}";
                    AddLog($"Uploading folder {folder}…");

                    foreach (string file in Directory.EnumerateFiles(
                        value, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string relative = Path.GetRelativePath(value, file).Replace('\\', '/');
                        ComputeUploadResult result = await _compute
                            .UploadFileAsync(
                                provider.Id, file, $"{remoteRoot}/{relative}",
                                UploadProgress(relative), cancellationToken)
                            .ConfigureAwait(false);
                        files++;
                        if (files % 10 == 0)
                            AddLog($"Uploaded {files} file(s)…");
                        _ = result;
                    }

                    component.Args[key] = remoteRoot;
                }
            }
        }

        if (files > 0) AddLog($"Uploaded {files} input file(s).");
    }

    private async Task DownloadArtifactAsync(
        ComputeProvider provider,
        string jobId,
        string localPath,
        string? artifact,
        CancellationToken cancellationToken)
    {
        string? folder = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        await using Stream stream = await _compute.Client
            .DownloadArtifactAsync(provider, jobId, artifact, cancellationToken)
            .ConfigureAwait(false);
        await using var file = new FileStream(
            localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
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
