using SharpMind.Studio.Core.Inference;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ModelFileListTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sm-modelfiles-" + Guid.NewGuid().ToString("n"));

    private string Models => Path.Combine(_root, "Models");
    private string Training => Path.Combine(_root, "Training");

    public ModelFileListTests()
    {
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Training);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Find_SortsByName_NotByWriteTime()
    {
        Touch("Models/zeta.gguf", DateTime.UtcNow.AddDays(-3));
        Touch("Models/alpha.gguf", DateTime.UtcNow.AddDays(-1));
        Touch("Models/mike.gguf", DateTime.UtcNow.AddDays(-2));

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Training, ".smlora");

        Assert.Equal(["alpha.gguf", "mike.gguf", "zeta.gguf"], files.Select(file => file.Name));
    }

    [Fact]
    public void Find_SeesModelsInSubfolders()
    {
        Touch("Models/qwen2.5-0.5b-instruct-q8_0.gguf", DateTime.UtcNow);
        Touch("Models/archive/older.smm", DateTime.UtcNow.AddDays(-30));

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Training, ".smlora");

        Assert.Contains(files, file => file.Name == "older.smm");
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Find_KeepsTheTrainingFolderTopLevelOnly()
    {
        Touch("Training/run-a.smlora", DateTime.UtcNow);
        Touch("Training/run-b/checkpoints/step-100.smm", DateTime.UtcNow);

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Training, ".smlora");

        Assert.Single(files);
        Assert.Equal("run-a.smlora", files[0].Name);
    }

    [Fact]
    public void Find_IncludesAdaptersButNotOtherFiles()
    {
        Touch("Training/base.smm", DateTime.UtcNow);
        Touch("Training/tuned.smlora", DateTime.UtcNow);
        Touch("Training/notes.txt", DateTime.UtcNow);
        Touch("Training/job.smmt", DateTime.UtcNow);

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Training, ".smlora");

        Assert.Equal(["base.smm", "tuned.smlora"], files.Select(file => file.Name));
    }

    [Fact]
    public void Find_DoesNotTruncate()
    {
        for (int i = 0; i < 60; i++)
        {
            Touch($"Models/model-{i:00}.gguf", DateTime.UtcNow.AddMinutes(-i));
        }

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Training, ".smlora");

        Assert.Equal(60, files.Count);
        // The least recently written file is the one the old cap dropped.
        Assert.Contains(files, file => file.Name == "model-59.gguf");
    }

    [Fact]
    public void Find_DeduplicatesWhenBothFoldersAreTheSame()
    {
        Touch("Models/only.gguf", DateTime.UtcNow);

        IReadOnlyList<FileInfo> files = ModelFileList.Find(Models, Models, ".smlora");

        Assert.Single(files);
    }

    [Fact]
    public void Find_ReturnsEmptyForMissingFolders()
    {
        IReadOnlyList<FileInfo> files = ModelFileList.Find(
            Path.Combine(_root, "nope"), Path.Combine(_root, "also-nope"), ".smlora");

        Assert.Empty(files);
    }

    private void Touch(string relativePath, DateTime writtenUtc)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }
}
