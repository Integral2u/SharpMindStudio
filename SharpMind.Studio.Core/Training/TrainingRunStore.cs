using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// One completed training run, persisted so the lab can compare experiments
/// after the process that produced them has gone. The curve is the same set of
/// logged points the live chart drew; the job settings are a compact summary,
/// not a copy of the full `.smmt`.
/// </summary>
public sealed class TrainingRunRecord
{
    /// <summary>Stable identifier for this run (new GUID per finish).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "run";
    public string? JobPath { get; set; }
    public string? ExportPath { get; set; }
    public string? AdapterPath { get; set; }

    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>True for a LoRA fine-tune; <see cref="BaseModelPath"/> is then set.</summary>
    public bool FineTune { get; set; }
    public string? BaseModelPath { get; set; }

    /// <summary>Configured number of steps.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Last step the loop actually reached.</summary>
    public int FinalStep { get; set; }

    public double ValidationPercent { get; set; }
    public double? BestValLoss { get; set; }
    public int? BestValStep { get; set; }

    /// <summary>Last logged training loss, when the run logged at least one step.</summary>
    public double? FinalLoss { get; set; }

    public double LearningRate { get; set; }
    public int HiddenDim { get; set; }
    public int BatchSize { get; set; }
    public int SeqLen { get; set; }

    public double ElapsedSeconds { get; set; }
    public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Size of the exported model, when one was written.</summary>
    public long ExportBytes { get; set; }

    public List<TrainingCurvePoint> Curve { get; set; } = [];

    /// <summary>
    /// Snapshot from the live service. Kept here so both the finish path and a
    /// future background runner build records the same way.
    /// </summary>
    public static TrainingRunRecord From(
        TrainingJobSettings job,
        TrainingRunResult result,
        IReadOnlyList<TrainingCurvePoint> curve,
        TimeSpan elapsed,
        string? exportPath)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);

        long bytes = 0;
        string? outputPath = exportPath ?? result.AdapterPath;
        if (outputPath is not null && File.Exists(outputPath))
        {
            try { bytes = new FileInfo(outputPath).Length; } catch { }
        }

        return new TrainingRunRecord
        {
            Name = job.Name,
            JobPath = job.SourcePath,
            ExportPath = exportPath,
            AdapterPath = result.AdapterPath,
            Success = result.Success,
            Error = result.Error,
            FineTune = job.IsFineTune,
            BaseModelPath = job.BaseModelPath,
            TotalSteps = job.TotalSteps,
            FinalStep = result.FinalStep,
            ValidationPercent = job.ValidationPercent,
            BestValLoss = result.BestValLoss,
            BestValStep = result.BestValStep,
            FinalLoss = curve.Count > 0 ? curve[^1].Loss : null,
            LearningRate = job.LearningRate,
            HiddenDim = job.HiddenDim,
            BatchSize = job.BatchSize,
            SeqLen = job.SeqLen,
            ElapsedSeconds = elapsed.TotalSeconds,
            ExportBytes = bytes,
            Curve = [.. curve],
        };
    }
}

/// <summary>A persisted run together with the file it lives in.</summary>
public sealed record TrainingRunSummary(string Path, TrainingRunRecord Record)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// JSON store for <see cref="TrainingRunRecord"/> under the Training folder.
/// Saving is best-effort: a run must never fail because its history file could
/// not be written.
/// </summary>
public static class TrainingRunStore
{
    public static string Folder => Path.Combine(StudioPaths.Training, "run-history");

    public static string Save(TrainingRunRecord record, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        folder ??= Folder;
        Directory.CreateDirectory(folder);

        string path = Path.Combine(
            folder,
            $"{record.FinishedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{Sanitize(record.Name)}-{record.Id[..Math.Min(8, record.Id.Length)]}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record, JsonOptions));
        return path;
    }

    public static IReadOnlyList<TrainingRunSummary> List(string? folder = null)
    {
        folder ??= Folder;
        if (!Directory.Exists(folder)) return [];

        var runs = new List<TrainingRunSummary>();
        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var record = System.Text.Json.JsonSerializer.Deserialize<TrainingRunRecord>(
                    File.ReadAllText(path), JsonOptions);
                if (record is not null) runs.Add(new TrainingRunSummary(path, record));
            }
            catch
            {
                // A half-written or hand-edited file must not hide the rest.
            }
        }

        return [.. runs
            .OrderByDescending(run => run.Record.FinishedUtc)
            .Take(200)];
    }

    public static void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); } catch { }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "run" : name.Trim();
    }
}
