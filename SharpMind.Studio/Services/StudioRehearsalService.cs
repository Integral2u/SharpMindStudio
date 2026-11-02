using System.Diagnostics;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;

namespace SharpMind.Studio.Services;

/// <summary>Measured result of a short rehearsal run.</summary>
public sealed record RehearsalResult(
    string JobName,
    int StepsRun,
    double MedianStepSeconds,
    double TokensPerSecond,
    int TotalSteps,
    TimeSpan EstimatedTotalTime,
    long PeakWorkingSetBytes,
    float? LastLoss,
    DateTime FinishedUtc)
{
    public double EstimatedTotalHours => EstimatedTotalTime.TotalHours;
}

/// <summary>
/// Runs a disposable ~30-step rehearsal of a job: same sources, tokenizer,
/// model, optimizer and QAT target, but no export, no merge and temp
/// checkpoints. It measures step time, tokens/s and working set, then scales the
/// measured step time to the job's full step count.
/// </summary>
public sealed class StudioRehearsalService(StudioTrainingService training, StudioExamService exam) : IDisposable
{
    public const int MaxSteps = 30;

    private readonly Lock _gate = new();
    private readonly StudioTrainingService _training = training;
    private readonly StudioExamService _exam = exam;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string _status = "No rehearsal yet.";
    private string? _error;
    private float _progress;
    private RehearsalResult? _result;
    private string? _jobName;

    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    public float Progress { get { lock (_gate) return _progress; } }
    public RehearsalResult? Result { get { lock (_gate) return _result; } }
    public string? JobName { get { lock (_gate) return _jobName; } }

    /// <summary>Starts a rehearsal. Returns false when a training run or exam is active.</summary>
    public bool Start(TrainingJobSettings job, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_training.IsRunning || _exam.IsRunning) return false;

        TrainingJobSettings clone = job.Clone();
        clone.Rehearsal = true;
        clone.Name = job.Name;
        clone.TotalSteps = Math.Clamp(job.TotalSteps, 1, MaxSteps);
        clone.LogInterval = 1;
        clone.CheckpointInterval = 0;
        clone.KeepRecent = -1;
        clone.StartFresh = true;
        clone.ResumeFrom = null;
        clone.WarmRestartFrom = null;
        clone.IncrementalMode = false;
        clone.ValidationPercent = 0;
        clone.TestPercent = 0;
        clone.ExportPath = null;
        clone.MergeOnExport = false;
        clone.SaveAdapterFile = false;

        lock (_gate)
        {
            if (_running) return false;
            _running = true;
            _error = null;
            _result = null;
            _progress = 0;
            _jobName = job.Name;
            _status = $"Rehearsing {clone.TotalSteps} steps...";
            _cts = new CancellationTokenSource();
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunCoreAsync(clone, job.TotalSteps, pluginsFolder, _cts.Token));
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
                _status = "Interrupting the rehearsal...";
            }
        }
        Changed?.Invoke();
    }

    private async Task RunCoreAsync(
        TrainingJobSettings rehearsal,
        int fullSteps,
        string pluginsFolder,
        CancellationToken cancellationToken)
    {
        var steps = new List<TrainStepResult>();
        long peakWorkingSet = 0;
        using var sampler = new Timer(_ =>
        {
            long working = Environment.WorkingSet;
            lock (_gate)
            {
                if (working > peakWorkingSet) peakWorkingSet = working;
            }
        }, null, 100, 100);

        try
        {
            TrainingRunResult result = await TrainingRunner.RunAsync(
                rehearsal,
                pluginsFolder,
                new Relay<string>(line =>
                {
                    lock (_gate) _status = line;
                    Changed?.Invoke();
                }),
                new Relay<float>(value =>
                {
                    lock (_gate) _progress = Math.Clamp(value, 0, 1);
                    Changed?.Invoke();
                }),
                step =>
                {
                    lock (_gate) steps.Add(step);
                    Changed?.Invoke();
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                lock (_gate)
                {
                    _error = result.Error;
                    _status = string.Equals(result.Error, "cancelled", StringComparison.OrdinalIgnoreCase)
                        ? "Rehearsal interrupted."
                        : "Rehearsal failed.";
                }
                return;
            }

            List<TrainStepResult> measured = steps.Count > 1 ? [.. steps.Skip(1)] : steps;
            if (measured.Count == 0)
                measured = [.. steps];

            double medianStep = Median(measured.Select(step => step.StepTime.TotalSeconds));
            double tokensPerSecond = Median(measured
                .Select(step => (double)step.TokensPerSecond)
                .Where(value => value > 0));
            var estimated = TimeSpan.FromSeconds(medianStep * Math.Max(1, fullSteps));

            var rehearsalResult = new RehearsalResult(
                rehearsal.Name,
                steps.Count,
                medianStep,
                tokensPerSecond,
                fullSteps,
                estimated,
                peakWorkingSet,
                steps.LastOrDefault()?.Loss,
                DateTime.UtcNow);

            lock (_gate)
            {
                _result = rehearsalResult;
                _progress = 1;
                _status = $"{medianStep:0.00}s/step, {tokensPerSecond:0.0} tok/s, "
                          + $"~{Duration(estimated)} for {fullSteps} steps.";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _status = "Rehearsal interrupted.";
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _error = $"{ex.GetType().Name}: {ex.Message}";
                _status = "Rehearsal failed.";
            }
        }
        finally
        {
            sampler.Dispose();
            lock (_gate)
            {
                _running = false;
                _cts?.Dispose();
                _cts = null;
            }
            Changed?.Invoke();
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.OrderBy(value => value)];
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static string Duration(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{span.TotalHours:0.0} h"
            : span.TotalMinutes >= 1
                ? $"{span.TotalMinutes:0} min"
                : $"{span.TotalSeconds:0} s";

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
