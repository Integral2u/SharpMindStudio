using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Projects;

/// <summary>
/// A project is a folder with a stable identity and the four working
/// directories the first Studio slice needs. All paths come from here so a
/// future UI never concatenates them itself.
/// </summary>
public sealed class StudioProject
{
    public const string ProjectFileName = "project.json";

    private StudioProject(string root, ProjectDocument document)
    {
        Root = root;
        Document = document;
    }

    public string Root { get; }
    public ProjectDocument Document { get; private set; }

    public string ProjectFile => Path.Combine(Root, ProjectFileName);
    public string DataDirectory => Path.Combine(Root, "data");
    public string RunsDirectory => Path.Combine(Root, "runs");
    public string ModelsDirectory => Path.Combine(Root, "models");
    public string ModelListsDirectory => Path.Combine(Root, "model-lists");
    public string EvalsDirectory => Path.Combine(Root, "evals");

    public static bool IsProject(string root) =>
        File.Exists(Path.Combine(Path.GetFullPath(root), ProjectFileName));

    public static StudioProject Create(string root, string name, string? deviceProfile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string fullRoot = Path.GetFullPath(root);
        string projectFile = Path.Combine(fullRoot, ProjectFileName);
        if (File.Exists(projectFile))
            throw new InvalidOperationException($"'{fullRoot}' already contains a Studio project.");

        var document = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
            DeviceProfile = string.IsNullOrWhiteSpace(deviceProfile) ? null : deviceProfile.Trim(),
        };

        var project = new StudioProject(fullRoot, document);
        project.EnsureDirectories();
        project.Save();
        return project;
    }

    public static StudioProject Open(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullRoot = Path.GetFullPath(root);
        string projectFile = Path.Combine(fullRoot, ProjectFileName);
        if (!File.Exists(projectFile))
            throw new FileNotFoundException($"No Studio project found at '{fullRoot}'.", projectFile);

        ProjectDocument document = JsonFile.Read<ProjectDocument>(projectFile);
        if (document.Schema != ProjectDocument.CurrentSchema)
            throw new InvalidDataException(
                $"Project schema '{document.Schema}' is not supported (expected '{ProjectDocument.CurrentSchema}').");

        var project = new StudioProject(fullRoot, document);
        project.EnsureDirectories();
        return project;
    }

    public void Save() => JsonFile.Write(ProjectFile, Document);

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RunsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(ModelListsDirectory);
        Directory.CreateDirectory(EvalsDirectory);
    }
}

