using System.Collections.Concurrent;
using SharpMind.Studio.Core.Training;
using SharpMind.Training;

namespace SharpMind.Studio.ComputeHost;

/// <summary>
/// Runs training jobs in the background and keeps their status/logs in memory.
/// A job's data and model paths are host paths; mount the data volume.
/// </summary>
public sealed class TrainingJobManager
{
    private readonly ConcurrentDictionary<string, TrainingJobState> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);

    public TrainingJobState Start(TrainingJobRequest request, ComputeOptions options)
    {
        string id = $"job-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
        var state = new TrainingJobState { Id = id, Status = "queued" };
        _jobs[id] = state;
        var cancellation = new CancellationTokenSource();
        _cancellations[id] = cancellation;

        _ = Task.Run(() => RunAsync(id, request, options, cancellation.Token));
        return state;
    }

    public TrainingJobState? Get(string id) =>
        _jobs.TryGetValue(id, out TrainingJobState? state) ? state : null;

    public IReadOnlyList<TrainingJobState> List() =>
        [.. _jobs.Values.OrderByDescending(job => job.CreatedUtc)];

    public bool Cancel(string id)
    {
        if (!_cancellations.TryGetValue(id, out CancellationTokenSource? cts)) return false;
        cts.Cancel();
        return true;
    }

    private async Task RunAsync(
        string id,
        TrainingJobRequest request,
        ComputeOptions options,
        CancellationToken cancellationToken)
    {
        TrainingJobState state = _jobs[id];
        state.Status = "running";
        state.StartedUtc = DateTimeOffset.UtcNow;

        var status = new InlineProgress<string>(line =>
        {
            lock (state.Log)
            {
                state.Log.Add(line);
                if (state.Log.Count > 2_000)
                    state.Log.RemoveRange(0, state.Log.Count - 2_000);
            }
        });
        var progress = new InlineProgress<float>(value => state.Progress = value);
        Action<TrainStepResult> onStep = step =>
        {
            state.Step = step.Step;
            state.Loss = step.Loss;
            state.ValLoss = step.ValLoss is { } value && double.IsFinite(value) ? value : null;
        };

        try
        {
            TrainingRunResult result = await TrainingRunner.RunAsync(
                request.Job,
                request.PluginsFolder ?? options.PluginsFolder,
                status,
                progress,
                onStep: onStep,
                cancellationToken: cancellationToken,
                recipeFolder: request.RecipeFolder).ConfigureAwait(false);

            state.Success = result.Success;
            state.ExportPath = result.ExportPath;
            state.AdapterPath = result.AdapterPath;
            state.Error = result.Error;
            state.Status = result.Success
                ? "succeeded"
                : string.Equals(result.Error, "cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : "failed";
        }
        catch (OperationCanceledException)
        {
            state.Status = "cancelled";
            state.Error = "cancelled";
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            status.Report($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            state.FinishedUtc = DateTimeOffset.UtcNow;
            _cancellations.TryRemove(id, out CancellationTokenSource? cts);
            cts?.Dispose();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
