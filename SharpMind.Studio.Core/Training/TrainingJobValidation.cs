using SharpMind.Training;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Shared checks and summary text for the job editor. The editor and the run
/// action use the same rules so a form that looks valid cannot fail on a
/// dimension the review did not flag.
/// </summary>
public static class TrainingJobValidation
{
    public static IReadOnlyList<string> Warnings(TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var warnings = new List<string>();

        if (job.HiddenDim <= 0 || job.NumLayers <= 0 || job.NumHeads <= 0
            || job.NumKvHeads <= 0 || job.FfnDim <= 0 || job.MaxSeqLen <= 0)
        {
            warnings.Add("Model dimensions and context length must be positive.");
        }
        else
        {
            if (job.HiddenDim % job.NumHeads != 0)
                warnings.Add($"HiddenDim ({job.HiddenDim}) must be divisible by NumHeads ({job.NumHeads}).");
            if (job.NumHeads % job.NumKvHeads != 0)
                warnings.Add($"NumHeads ({job.NumHeads}) must be divisible by NumKvHeads ({job.NumKvHeads}).");
            if (job.SeqLen > job.MaxSeqLen)
                warnings.Add($"Sequence length ({job.SeqLen}) exceeds the context ({job.MaxSeqLen}).");
        }

        if (job.ValidationPercent + job.TestPercent >= 100)
            warnings.Add("Validation and held-out exam splits together must stay below 100%.");

        if (job.Sources.Count == 0)
        {
            warnings.Add("Add at least one corpus source.");
        }
        else
        {
            if (job.Sources.Any(source => string.IsNullOrWhiteSpace(source.Component.TypeName)))
                warnings.Add("A source has no component type; remove it and add it again.");
            if (job.Sources.Any(source =>
                    source.Component.TypeName.Contains("TextFileSource", StringComparison.Ordinal)
                    && (!source.Component.Args.TryGetValue("path", out string? path)
                        || string.IsNullOrWhiteSpace(path))))
                warnings.Add("A text source has no path.");
        }

        if (TrainingModelOptions.ValidateQat(job.QuantAwareTraining, job.HiddenDim) is { } qatProblem)
            warnings.Add(qatProblem);

        if (job.TotalSteps <= 0)
            warnings.Add("Total steps must be positive.");

        return warnings;
    }

    public static string ParameterEstimate(TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            int vocab = Math.Max(1, job.VocabSize > 0 ? job.VocabSize : job.TokenizerVocabSize);
            var config = TrainingModelOptions.ResolveModelConfig(job, vocab);
            long count = ModelSizer.CountParameters(config);
            return count >= 1_000_000
                ? $"{count / 1_000_000d:0.0}M"
                : $"{count / 1_000d:0.0}k";
        }
        catch
        {
            return "-";
        }
    }

    public static string PrecisionLabel(TrainingJobSettings job)
        => string.IsNullOrWhiteSpace(job.QuantAwareTraining) ? "F32" : job.QuantAwareTraining;

    public static string DeviceLabel(TrainingJobSettings job)
        => string.IsNullOrWhiteSpace(job.Accelerator) ? "CPU" : job.Accelerator;

    public static string BatchLabel(TrainingJobSettings job)
        => $"batch {job.BatchSize} x {job.SeqLen}";
}
