using System.Text;

namespace SharpMind.Studio.Core.Runs;

/// <summary>Creates and opens run folders under a project's <c>runs/</c> directory.</summary>
public sealed class RunStore
{
    public const string EventsFileName = "events.jsonl";

    public RunStore(string runsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runsRoot);
        RunsRoot = Path.GetFullPath(runsRoot);
    }

    public string RunsRoot { get; }

    public RunHandle Create(string kind, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        Directory.CreateDirectory(RunsRoot);
        string slug = Slug($"{kind}-{subject}");
        string runId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{slug}-{Guid.NewGuid().ToString("n")[..8]}";
        string directory = Path.Combine(RunsRoot, runId);
        Directory.CreateDirectory(directory);
        return new RunHandle(runId, directory);
    }

    public RunHandle Open(string runIdOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runIdOrPath);
        string path = Path.GetFullPath(runIdOrPath);

        if (File.Exists(path))
        {
            if (!string.Equals(Path.GetFileName(path), EventsFileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Expected a run directory or '{EventsFileName}'.", nameof(runIdOrPath));
            path = Path.GetDirectoryName(path)!;
        }

        string candidate = Directory.Exists(path) ? path : Path.Combine(RunsRoot, runIdOrPath);
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException($"Run '{runIdOrPath}' was not found.");

        return new RunHandle(Path.GetFileName(candidate), candidate);
    }

    public void Delete(string runIdOrPath)
    {
        RunHandle handle = Open(runIdOrPath);
        string relative = Path.GetRelativePath(RunsRoot, handle.Directory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Refusing to delete a run outside the runs folder.");
        if (Directory.Exists(handle.Directory))
            Directory.Delete(handle.Directory, recursive: true);
    }

    public IReadOnlyList<RunHandle> List()
    {
        if (!Directory.Exists(RunsRoot)) return [];

        return [.. Directory.EnumerateDirectories(RunsRoot)
            .Select(path => new RunHandle(Path.GetFileName(path), path))
            .OrderByDescending(handle => handle.RunId, StringComparer.Ordinal)];
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool dash = false;
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "run" : slug[..Math.Min(32, slug.Length)].TrimEnd('-');
    }
}

public sealed class RunHandle
{
    internal RunHandle(string runId, string directory)
    {
        RunId = runId;
        Directory = directory;
    }

    public string RunId { get; }
    public string Directory { get; }
    public string EventsPath => Path.Combine(Directory, RunStore.EventsFileName);

    public RunEventWriter OpenWriter() => new(EventsPath);

    public IAsyncEnumerable<RunEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        RunEventReader.ReadAsync(EventsPath, cancellationToken);
}

