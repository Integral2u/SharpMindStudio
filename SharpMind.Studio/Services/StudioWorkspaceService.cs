using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Projects;
using SharpMind.Studio.Core.Runs;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record StudioProjectInfo(string Name, string Path, bool IsDefault);

public sealed record WorkspaceStateDocument
{
    public string? CurrentProject { get; init; }
}

/// <summary>
/// The project this local Studio instance is showing. The original project is
/// the default workspace; Work projects live beside it under SharpMind\Workspaces
/// and can be switched without restarting the app.
/// </summary>
public sealed class StudioWorkspaceService
{
    private readonly string _defaultProjectRoot;
    private readonly Lock _gate = new();

    public StudioWorkspaceService(IOptions<StudioOptions> options, IHostEnvironment environment)
    {
        StudioPaths.EnsureCreated();

        string configured = options.Value.ProjectRoot;
        _defaultProjectRoot = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(StudioPaths.Root, configured));

        MigrateLegacyDefaultProject();
        if (!SamePath(StudioPaths.Projects, _defaultProjectRoot))
            LegacyStudioCleanup.RemoveEmptyProjectFolders(StudioPaths.Projects);

        StudioProject defaultProject = StudioProject.IsProject(_defaultProjectRoot)
            ? StudioProject.Open(_defaultProjectRoot)
            : StudioProject.Create(_defaultProjectRoot, "Studio");

        string current = ReadCurrentProject() ?? defaultProject.Root;
        if (!CanOpen(current) || !StudioProject.IsProject(current))
            current = defaultProject.Root;

        SetProject(StudioProject.Open(current), save: false);
        ModelsFolder = options.Value.ResolvedModelsFolder;
        CorpusFolder = options.Value.ResolvedCorpusFolder;
        TrainingFolder = options.Value.ResolvedTrainingFolder;
        PluginsFolder = options.Value.ResolvedPluginsFolder;
    }

    public event Action? ProjectChanged;

    public StudioProject Project { get; private set; } = null!;
    public RunStore Runs { get; private set; } = null!;
    public ModelListStore ModelLists { get; private set; } = null!;
    public string ModelsFolder { get; }
    public string CorpusFolder { get; }
    public string TrainingFolder { get; }
    public string PluginsFolder { get; }
    public string DefaultProjectRoot => _defaultProjectRoot;

    private BuilderCatalogResult? _builders;
    public BuilderCatalogResult Builders => _builders ??= BuilderCatalog.Discover(PluginsFolder);

    private void MigrateLegacyDefaultProject()
    {
        string legacyRoot = Path.Combine(StudioPaths.Root, "Studio");
        string marker = Path.Combine(StudioPaths.Projects, "workspace-migrated");
        if (SamePath(legacyRoot, _defaultProjectRoot) || File.Exists(marker)) return;
        if (!StudioProject.IsProject(legacyRoot))
        {
            File.WriteAllText(marker, "no legacy project");
            return;
        }

        try
        {
            if (!StudioProject.IsProject(_defaultProjectRoot))
                StudioProject.Create(_defaultProjectRoot, "Studio");

            foreach (string directory in new[] { "chats", "runs", "model-lists", "data", "models", "evals" })
            {
                string source = Path.Combine(legacyRoot, directory);
                if (Directory.Exists(source))
                    MoveMissing(source, Path.Combine(_defaultProjectRoot, directory));
            }

            File.WriteAllText(marker, $"migrated from {legacyRoot}");
        }
        catch
        {
            // A failed migration must not prevent Studio opening the new default project.
        }
    }

    private static void MoveMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Move(file, destination);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(directory));
            if (Directory.Exists(destination)) MoveMissing(directory, destination);
            else Directory.Move(directory, destination);
        }
    }
    public IReadOnlyList<StudioProjectInfo> Projects
    {
        get
        {
            var projects = new List<StudioProjectInfo>();
            StudioProjectInfo? defaultProject = Describe(_defaultProjectRoot, isDefault: true);
            if (defaultProject is not null) projects.Add(defaultProject);

            if (Directory.Exists(StudioPaths.Workspaces))
            {
                foreach (string directory in Directory.EnumerateDirectories(StudioPaths.Workspaces))
                {
                    if (SamePath(directory, _defaultProjectRoot)) continue;
                    StudioProjectInfo? project = Describe(directory, isDefault: false);
                    if (project is not null) projects.Add(project);
                }
            }

            return [.. projects
                .OrderByDescending(project => project.IsDefault)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public StudioProject CreateProject(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(StudioPaths.Workspaces);

        string slug = Slug(name);
        string root = Path.Combine(StudioPaths.Workspaces, slug);
        for (int suffix = 2; Directory.Exists(root) || File.Exists(root); suffix++)
            root = Path.Combine(StudioPaths.Workspaces, $"{slug}-{suffix}");

        return StudioProject.Create(root, name.Trim());
    }

    public void SwitchProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!CanOpen(full))
            throw new InvalidOperationException("Only the default project or a Work project can be opened.");
        if (!StudioProject.IsProject(full))
            throw new FileNotFoundException("No Studio project was found at that path.", full);

        lock (_gate)
        {
            if (SamePath(full, Project.Root)) return;
            SetProject(StudioProject.Open(full), save: true);
        }
    }

    private IReadOnlyList<RunSummary>? _runCache;
    private DateTimeOffset _runCacheUtc;

    public async Task<IReadOnlyList<RunSummary>> RecentRunsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runCache is not null && DateTimeOffset.UtcNow - _runCacheUtc < TimeSpan.FromSeconds(3))
                return _runCache;
        }

        IReadOnlyList<RunSummary> runs = await RunCatalog.ListAsync(Runs, cancellationToken);
        lock (_gate)
        {
            _runCache = runs;
            _runCacheUtc = DateTimeOffset.UtcNow;
        }
        return runs;
    }

    public void InvalidateRuns()
    {
        lock (_gate) _runCache = null;
    }

    public string? ResolveModelPath(string? modelPathOrName)
    {
        if (string.IsNullOrWhiteSpace(modelPathOrName)) return null;
        string candidate = modelPathOrName.Trim();
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        string direct = Path.Combine(ModelsFolder, candidate);
        if (File.Exists(direct)) return Path.GetFullPath(direct);
        if (!Directory.Exists(ModelsFolder)) return null;

        return Directory.EnumerateFiles(ModelsFolder, "*.*", SearchOption.AllDirectories)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(
                Path.GetFileName(candidate), StringComparison.OrdinalIgnoreCase));
    }

    private void SetProject(StudioProject project, bool save)
    {
        Project = project;
        Runs = new RunStore(project.RunsDirectory);
        ModelLists = new ModelListStore(project.ModelListsDirectory);
        _runCache = null;
        if (save) SaveCurrentProject(project.Root);
        ProjectChanged?.Invoke();
    }

    private bool CanOpen(string path) =>
        SamePath(path, _defaultProjectRoot) ||
        IsUnder(path, StudioPaths.Workspaces);

    private static StudioProjectInfo? Describe(string root, bool isDefault)
    {
        try
        {
            if (!StudioProject.IsProject(root)) return null;
            StudioProject project = StudioProject.Open(root);
            return new StudioProjectInfo(project.Document.Name, project.Root, isDefault);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadCurrentProject()
    {
        try
        {
            if (!File.Exists(StudioPaths.WorkspaceFile)) return null;
            return JsonFile.Read<WorkspaceStateDocument>(StudioPaths.WorkspaceFile).CurrentProject;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCurrentProject(string path)
    {
        try
        {
            JsonFile.Write(StudioPaths.WorkspaceFile, new WorkspaceStateDocument { CurrentProject = path });
        }
        catch
        {
        }
    }

    private static string Slug(string name)
    {
        string slug = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "project" : slug[..Math.Min(slug.Length, 48)];
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        string full = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}



