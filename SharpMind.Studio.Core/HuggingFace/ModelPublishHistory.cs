using System.Text.Json;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.HuggingFace;

/// <summary>One successful HuggingFace publish, kept across app restarts.</summary>
public sealed record ModelPublishRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string RepoId { get; init; }
    public required string RepoUrl { get; init; }
    public required string CommitUrl { get; init; }
    public string? CommitOid { get; init; }
    public required string ArtifactFileName { get; init; }
    public string? SmmFileName { get; init; }
    public string Quantization { get; init; } = "";
    public bool Private { get; init; }
    public DateTimeOffset PublishedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? SourceFileName { get; init; }
    public string? SourceChecksum { get; init; }
    public long ArtifactBytes { get; init; }
    public long SmmBytes { get; init; }

    public string RepoUrlWithSettings => $"{RepoUrl}/settings";
}

/// <summary>
/// Small JSON history under the Training folder. It lets the Publish tab show
/// the last repository and commit after a tab switch or app restart.
/// </summary>
public sealed class ModelPublishHistoryStore
{
    private const int MaxRecords = 100;
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ModelPublishHistoryStore(string? path = null)
    {
        FilePath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(StudioPaths.Training, "publish-history.json")
            : Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public IReadOnlyList<ModelPublishRecord> Load()
    {
        lock (Gate) return LoadLocked();
    }

    public ModelPublishRecord Add(ModelPublishRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (Gate)
        {
            var records = new List<ModelPublishRecord>(LoadLocked());
            records.RemoveAll(existing =>
                string.Equals(existing.CommitUrl, record.CommitUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ArtifactFileName, record.ArtifactFileName,
                    StringComparison.OrdinalIgnoreCase));
            records.Insert(0, record);
            SaveLocked(records.Take(MaxRecords));
            return record;
        }
    }

    private IReadOnlyList<ModelPublishRecord> LoadLocked()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            List<ModelPublishRecord>? records =
                JsonSerializer.Deserialize<List<ModelPublishRecord>>(
                    File.ReadAllText(FilePath), JsonOptions);
            return [.. (records ?? [])
                .Where(record => !string.IsNullOrWhiteSpace(record.RepoId))
                .OrderByDescending(record => record.PublishedUtc)
                .Take(MaxRecords)];
        }
        catch
        {
            return [];
        }
    }

    public void Clear()
    {
        lock (Gate)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }

    private void SaveLocked(IEnumerable<ModelPublishRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records.ToList(), JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
