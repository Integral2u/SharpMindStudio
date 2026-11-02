using SharpMind.Data.Sources;

namespace SharpMind.Studio.Core.Training;

/// <summary>Rough corpus size before tokenization: files and bytes, with a token guess.</summary>
public sealed record TrainingCorpusEstimate(long Files, long Bytes, long? Tokens)
{
    public double Megabytes => Bytes / 1048576d;
    public static TrainingCorpusEstimate Empty { get; } = new(0, 0, null);
}

/// <summary>
/// Counts the files a text source would read and estimates tokens from bytes
/// (about four characters per token for ordinary text). It is deliberately a
/// size fact, not a trained tokenizer; the rehearsal is what measures real
/// throughput.
/// </summary>
public static class TrainingCorpusFacts
{
    public static TrainingCorpusEstimate Estimate(TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);

        long files = 0;
        long bytes = 0;

        foreach (JobSource source in job.Sources)
        {
            if (!source.Component.Args.TryGetValue("path", out string? path)
                || string.IsNullOrWhiteSpace(path))
                continue;

            IEnumerable<string> resolved;
            try
            {
                if (Directory.Exists(path))
                    resolved = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                else
                    resolved = GlobResolver.Resolve(path);
            }
            catch
            {
                continue;
            }

            foreach (string file in resolved)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    files++;
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // A vanished or locked file simply does not contribute.
                }
            }
        }

        return new TrainingCorpusEstimate(
            files,
            bytes,
            bytes > 0 ? bytes / 4 : null);
    }
}
