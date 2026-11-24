using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharpMind.Studio.Core.Projects;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

[Collection("studio-paths")]
public sealed class StudioWorkspaceArchiveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StudioWorkspaceService _workspace;

    public StudioWorkspaceArchiveTests()
    {
        StudioPaths.UseRoot(_temp.Path);
        var options = Options.Create(new StudioOptions
        {
            ProjectRoot = _temp.Combine("Project"),
            ModelsFolder = _temp.Combine("Models"),
            CorpusFolder = _temp.Combine("Corpus"),
            TrainingFolder = _temp.Combine("Training"),
            PluginsFolder = _temp.Combine("Plugins"),
        });
        _workspace = new StudioWorkspaceService(
            options, new TestHostEnvironment { ContentRootPath = _temp.Path });
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ArchiveRestoreAndDelete_KeepTheProjectFilesRecoverable()
    {
        StudioProject project = _workspace.CreateProject("Dotnet Test");
        Assert.Contains(_workspace.Projects, item => item.Path == project.Root);
        Assert.True(File.Exists(project.ProjectFile));

        _workspace.ArchiveProject(project.Root);

        Assert.DoesNotContain(_workspace.Projects, item => item.Path == project.Root);
        StudioProjectInfo archived = Assert.Single(
            _workspace.ArchivedProjects.Where(item => item.Name == "Dotnet Test"));
        Assert.True(Directory.Exists(archived.Path));
        Assert.NotNull(_workspace.FindArchivedProject("dotnet test"));

        StudioProject restored = _workspace.RestoreArchivedProject(archived.Path);

        Assert.Contains(_workspace.Projects, item => item.Path == restored.Root);
        Assert.Empty(_workspace.ArchivedProjects);
        Assert.True(File.Exists(restored.ProjectFile));

        _workspace.ArchiveProject(restored.Root);
        StudioProjectInfo second = Assert.Single(_workspace.ArchivedProjects);
        _workspace.DeleteArchivedProject(second.Path);

        Assert.Empty(_workspace.ArchivedProjects);
        Assert.False(Directory.Exists(second.Path));
    }

    [Fact]
    public void DeleteProject_RemovesTheWholeRoot()
    {
        StudioProject project = _workspace.CreateProject("Disposable");
        File.WriteAllText(Path.Combine(project.Root, "keep.txt"), "x");

        _workspace.DeleteProject(project.Root);

        Assert.False(Directory.Exists(project.Root));
        Assert.DoesNotContain(_workspace.Projects, item => item.Name == "Disposable");
    }

    [Fact]
    public void MoveSessions_MovesNewFilesAndSkipsConflicts()
    {
        StudioProject source = _workspace.CreateProject("Source");
        StudioProject target = _workspace.CreateProject("Target");
        File.WriteAllText(Path.Combine(source.ChatsDirectory, "one.json"), "{}");
        File.WriteAllText(Path.Combine(source.ChatsDirectory, "conflict.json"), "{}");
        File.WriteAllText(Path.Combine(target.ChatsDirectory, "conflict.json"), "{}");

        (int moved, int skipped) = _workspace.MoveSessions(source.Root, target.Root);

        Assert.Equal(1, moved);
        Assert.Equal(1, skipped);
        Assert.True(File.Exists(Path.Combine(target.ChatsDirectory, "one.json")));
        Assert.False(File.Exists(Path.Combine(source.ChatsDirectory, "one.json")));
        Assert.True(File.Exists(Path.Combine(source.ChatsDirectory, "conflict.json")));
        Assert.Equal(
            "{}",
            File.ReadAllText(Path.Combine(target.ChatsDirectory, "conflict.json")));
    }

    [Fact]
    public void MergeProjectData_CopiesMissingFilesAndKeepsTargetConflicts()
    {
        StudioProject source = _workspace.CreateProject("Source data");
        StudioProject target = _workspace.CreateProject("Target data");
        string sourceData = Path.Combine(source.DataDirectory, "notes");
        string targetData = Path.Combine(target.DataDirectory, "notes");
        Directory.CreateDirectory(sourceData);
        Directory.CreateDirectory(targetData);
        File.WriteAllText(Path.Combine(sourceData, "one.txt"), "source");
        File.WriteAllText(Path.Combine(sourceData, "same.txt"), "source");
        File.WriteAllText(Path.Combine(targetData, "same.txt"), "target");

        (int copied, int skipped) = _workspace.MergeProjectData(source.Root, target.Root);

        Assert.Equal(1, copied);
        Assert.Equal(1, skipped);
        Assert.Equal("source", File.ReadAllText(Path.Combine(targetData, "one.txt")));
        Assert.Equal("target", File.ReadAllText(Path.Combine(targetData, "same.txt")));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
