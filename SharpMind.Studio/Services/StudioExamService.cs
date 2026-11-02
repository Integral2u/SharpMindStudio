using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs the held-out exam for one persisted training run. The exam loads the
/// run's exported .smm and its saved .smmt job, scores the deterministic test
/// slice, writes the result back into the run record and refreshes history.
/// One exam at a time; the UI polls the same state whether or not the Runs tab
/// stays open.
/// </summary>
public sealed class StudioExamService(StudioTrainingService training)
{
    private readonly Lock _gate = new();
    private readonly StudioTrainingService _training = training;
    private bool _running;
    private string _status = "No exam run yet.";
    private string? _error;
    private string? _runPath;
    private TrainingExamResult? _result;

    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    public string? RunPath { get { lock (_gate) return _runPath; } }
    public TrainingExamResult? Result { get { lock (_gate) return _result; } }

    /// <summary>Starts an exam. Returns false when one is already running.</summary>
    public bool Start(TrainingRunSummary run, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            if (_running) return false;
            _running = true;
            _error = null;
            _result = null;
            _runPath = run.Path;
            _status = $"Examining {run.Record.Name}...";
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunCoreAsync(run, pluginsFolder));
        return true;
    }

    private async Task RunCoreAsync(TrainingRunSummary run, string pluginsFolder)
    {
        try
        {
            string? jobPath = run.Record.JobPath;
            if (string.IsNullOrWhiteSpace(jobPath) || !File.Exists(jobPath))
                throw new InvalidOperationException(
                    "This run has no saved .smmt job, so its corpus and exam split are unknown.");

            TrainingJobSettings? job = TrainingJobSettings.Load(jobPath, out string? loadError);
            if (job is null)
                throw new InvalidOperationException(loadError ?? "Could not load the run's job.");

            string? modelPath = run.Record.ExportPath;
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new InvalidOperationException(
                    "This run has no merged .smm to examine. Run the fine-tune with a merged export first.");

            TrainingExamResult result = await TrainingExamRunner.RunAsync(
                job,
                modelPath,
                pluginsFolder,
                line =>
                {
                    lock (_gate) _status = line;
                    Changed?.Invoke();
                }).ConfigureAwait(false);

            run.Record.ExamLoss = result.Loss;
            run.Record.ExamPerplexity = result.Perplexity;
            run.Record.ExamBatches = result.Batches;
            run.Record.ExamTokens = result.Tokens;
            run.Record.ExamEvaluatedUtc = result.EvaluatedUtc;
            TrainingRunStore.Update(run.Path, run.Record);
            _training.RefreshRunHistory();

            lock (_gate)
            {
                _result = result;
                _status = $"Exam: loss {result.Loss:0.0000}, ppl {result.Perplexity:0.00}.";
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _error = ex.Message;
                _status = "Exam failed.";
            }
        }
        finally
        {
            lock (_gate) _running = false;
            Changed?.Invoke();
        }
    }
}
