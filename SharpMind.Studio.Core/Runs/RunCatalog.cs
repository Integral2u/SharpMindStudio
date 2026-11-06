using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Core.Runs;

/// <summary>Reads run folds into the summary rows the dashboard and CLI show.</summary>
public static class RunCatalog
{
    public static async Task<IReadOnlyList<RunSummary>> ListAsync(
        RunStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var summaries = new List<RunSummary>();
        foreach (var handle in store.List())
            summaries.Add(await LoadAsync(handle, cancellationToken).ConfigureAwait(false));
        return summaries;
    }

    public static async Task<RunSummary> LoadAsync(
        RunHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        RunStarted? started = null;
        RunFinished? finished = null;
        ProbeAnswered? probe = null;
        ToolProbeCompleted? toolProbe = null;
        DateTimeOffset? firstStep = null;
        DateTimeOffset? lastStep = null;
        double stepTps = 0;
        int steps = 0;

        await foreach (var runEvent in handle.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (runEvent)
            {
                case RunStarted value:
                    started = value;
                    break;
                case StepLogged value:
                    steps++;
                    firstStep ??= value.AtUtc;
                    lastStep = value.AtUtc;
                    stepTps += value.TokensPerSecond;
                    break;
                case ProbeAnswered value:
                    probe = value;
                    break;
                case ToolProbeCompleted value:
                    toolProbe = value;
                    break;
                case RunFinished value:
                    finished = value;
                    break;
            }
        }

        double tps = stepTps;
        if (steps > 1 && firstStep is { } first && lastStep is { } last && last > first)
            tps = (steps - 1) / (last - first).TotalSeconds;
        else if (steps > 0)
            tps = stepTps / steps;

        ModelFamily family = FamilyDetector.Detect(
            null,
            $"{started?.Subject} {started?.ModelPath}");

        return new RunSummary
        {
            RunId = handle.RunId,
            Kind = started?.Kind,
            Subject = started?.Subject,
            ModelPath = started?.ModelPath,
            FamilyId = family.Id,
            FamilyName = family.DisplayName,
            Prompt = started?.Prompt,
            WarmupTokens = started?.WarmupTokens,
            MeasureTokens = started?.MeasureTokens,
            InferenceVersion = started?.InferenceVersion,
            Device = started?.Device,
            Generator = started?.Generator,
            Cache = started?.Cache,
            StartedUtc = started?.AtUtc,
            FinishedUtc = finished?.AtUtc,
            Status = finished?.Status ?? "running",
            Steps = steps,
            TokensPerSecond = tps,
            ProbeText = probe?.Text,
            ToolStatus = toolProbe?.Status ?? ToolRunStatus(started, finished),
            ToolRunId = toolProbe?.ToolRunId,
            Error = finished?.Error,
        };
    }

    private static string ToolRunStatus(RunStarted? started, RunFinished? finished)
    {
        if (!string.Equals(started?.Kind, "tool-probe", StringComparison.OrdinalIgnoreCase))
            return "N/A";

        return finished?.Status switch
        {
            "succeeded" => "Yes",
            "failed" => "No",
            _ => "N/A",
        };
    }
}
