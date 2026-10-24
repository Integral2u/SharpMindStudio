using SharpMind.Studio.Core.Projects;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void Create_WritesIdentityAndWorkingDirectories()
    {
        using var temp = new TempDirectory();
        var project = StudioProject.Create(temp.Combine("project"), "Client A", "kiosk-4gb");

        Assert.True(File.Exists(project.ProjectFile));
        Assert.True(Directory.Exists(project.DataDirectory));
        Assert.True(Directory.Exists(project.RunsDirectory));
        Assert.True(Directory.Exists(project.ModelsDirectory));
        Assert.True(Directory.Exists(project.ModelListsDirectory));
        Assert.True(Directory.Exists(project.EvalsDirectory));
        Assert.Equal("Client A", project.Document.Name);
        Assert.Equal("kiosk-4gb", project.Document.DeviceProfile);
        Assert.False(string.IsNullOrWhiteSpace(project.Document.Id));
    }

    [Fact]
    public void Open_ReadsTheSameIdentity()
    {
        using var temp = new TempDirectory();
        var created = StudioProject.Create(temp.Combine("project"), "Client A");
        var opened = StudioProject.Open(created.Root);

        Assert.Equal(created.Document.Id, opened.Document.Id);
        Assert.Equal(created.Document.Name, opened.Document.Name);
    }

    [Fact]
    public void Create_RefusesToOverwriteAnExistingProject()
    {
        using var temp = new TempDirectory();
        StudioProject.Create(temp.Combine("project"), "First");

        var error = Assert.Throws<InvalidOperationException>(
            () => StudioProject.Create(temp.Combine("project"), "Second"));
        Assert.Contains("already contains", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_RejectsAnUnknownSchema()
    {
        using var temp = new TempDirectory();
        var project = StudioProject.Create(temp.Combine("project"), "Client A");
        var document = project.Document with { Schema = "studio-project/999" };
        JsonFile.Write(project.ProjectFile, document);

        var error = Assert.Throws<InvalidDataException>(() => StudioProject.Open(project.Root));
        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ModelListStoreTests
{
    [Fact]
    public void Save_TrimsAndDeduplicatesModelPaths()
    {
        using var temp = new TempDirectory();
        var store = new ModelListStore(temp.Combine("lists"));

        ModelList list = store.Save("  Small models  ", ["a.gguf", " a.gguf ", "", "b.smm"]);

        Assert.Equal("Small models", list.Name);
        Assert.Equal(new[] { "a.gguf", "b.smm" }, list.Models);
        Assert.Single(store.List());
    }

    [Fact]
    public void Save_RejectsAnEmptyList()
    {
        using var temp = new TempDirectory();
        var store = new ModelListStore(temp.Combine("lists"));

        Assert.Throws<ArgumentException>(() => store.Save("empty", []));
    }

    [Fact]
    public void Delete_RemovesTheSavedList()
    {
        using var temp = new TempDirectory();
        var store = new ModelListStore(temp.Combine("lists"));
        store.Save("keep", ["a.gguf"]);

        store.Delete("keep");

        Assert.Empty(store.List());
    }
}


