namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Creates a child job from a finished run. The fork keeps the parent's settings
/// and data, points at the parent checkpoint when one exists, and carries the
/// lineage fields the Runs view uses to show and compare branches.
///
/// This is the honest first graph slice: it reuses the checkpoint/tokenizer
/// artifacts the run already produced. Content-addressed step reuse comes with
/// the Recipe graph later.
/// </summary>
public static class TrainingFork
{
    public static TrainingJobSettings Create(
        TrainingRunRecord parent,
        TrainingJobSettings job,
        string? latestCheckpoint = null,
        int? latestStep = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(parent.Id))
            throw new ArgumentException("The parent run has no id.", nameof(parent));

        TrainingJobSettings clone = job.Clone();
        clone.Name = ForkName(job.Name);
        clone.SourcePath = null;
        clone.ExportPath = null;
        clone.SourceFileHashes.Clear();
        clone.ResumeFrom = null;
        clone.WarmRestartFrom = null;
        clone.StartFresh = true;

        clone.ForkedFromRunId = parent.Id;
        clone.ForkRootId = string.IsNullOrWhiteSpace(parent.ForkRootId) ? parent.Id : parent.ForkRootId;
        clone.ForkPoint = parent.FinalStep > 0 ? $"step {parent.FinalStep}" : "finished run";
        clone.Variant = "fork";

        if (!string.IsNullOrWhiteSpace(latestCheckpoint))
        {
            if (job.IsFineTune)
                clone.ResumeFrom = latestCheckpoint;
            else
                clone.WarmRestartFrom = latestCheckpoint;

            clone.StartFresh = false;
            if (latestStep is > 0 and int step && clone.TotalSteps <= step)
                clone.TotalSteps = step + Math.Max(10, step / 4);
        }

        return clone;
    }

    private static string ForkName(string name)
    {
        string stem = string.IsNullOrWhiteSpace(name) ? "model" : name.Trim();
        return $"{stem} fork {DateTime.Now:HHmmss}";
    }
}
