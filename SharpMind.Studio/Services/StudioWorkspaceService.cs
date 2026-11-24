using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SharpMind.Studio.Core.Chat;
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

    public IReadOnlyList<StudioProjectInfo> ArchivedProjects
    {
        get
        {
            if (!Directory.Exists(StudioPaths.WorkspaceArchives)) return [];
            return [.. Directory.EnumerateDirectories(StudioPaths.WorkspaceArchives)
                .Select(path => Describe(path, isDefault: false))
                .Where(project => project is not null)
                .Cast<StudioProjectInfo>()
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public StudioProjectInfo? FindArchivedProject(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ArchivedProjects.FirstOrDefault(project =>
            project.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Move a Work project out of the active list; its files stay recoverable.</summary>
    public void ArchiveProject(string path)
    {
        string full = ValidateWorkProject(path);
        if (SamePath(full, Project.Root)) SwitchProject(_defaultProjectRoot);

        Directory.CreateDirectory(StudioPaths.WorkspaceArchives);
        string baseName = Path.GetFileName(full.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string destination = Path.Combine(
            StudioPaths.WorkspaceArchives, $"{baseName}-{stamp}");
        for (int suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
            destination = Path.Combine(
                StudioPaths.WorkspaceArchives, $"{baseName}-{stamp}-{suffix}");

        Directory.Move(full, destination);
        NotifyProjectsChanged();
    }

    /// <summary>Permanently delete a Work project and every file under its root.</summary>
    public void DeleteProject(string path)
    {
        string full = ValidateWorkProject(path);
        if (SamePath(full, Project.Root)) SwitchProject(_defaultProjectRoot);
        Directory.Delete(full, recursive: true);
        NotifyProjectsChanged();
    }

    /// <summary>Move an archived project back under Workspaces and return it.</summary>
    public StudioProject RestoreArchivedProject(string archivePath)
    {
        string full = ValidateArchive(archivePath);
        StudioProject archived = StudioProject.Open(full);
        string slug = Slug(archived.Document.Name);
        string destination = Path.Combine(StudioPaths.Workspaces, slug);
        for (int suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
            destination = Path.Combine(StudioPaths.Workspaces, $"{slug}-{suffix}");

        Directory.CreateDirectory(StudioPaths.Workspaces);
        Directory.Move(full, destination);
        StudioProject restored = StudioProject.Open(destination);
        NotifyProjectsChanged();
        return restored;
    }

    public void DeleteArchivedProject(string archivePath)
    {
        string full = ValidateArchive(archivePath);
        Directory.Delete(full, recursive: true);
        NotifyProjectsChanged();
    }

    /// <summary>
    /// Move saved sessions from one project to another. A session file that
    /// already exists in the target is skipped; the caller reports the counts.
    /// </summary>
    /// <summary>
    /// Move one saved session to another Work project. A Chat session moved out
    /// of the default project is promoted to a Work session. Existing session
    /// folders in the target are refused rather than overwritten.
    /// </summary>
    public void MoveSession(string sourcePath, string targetPath, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string source = ValidateProjectPath(sourcePath);
        string target = ValidateProjectPath(targetPath);
        if (SamePath(source, target))
            throw new InvalidOperationException("Choose a different target project.");
        if (SamePath(target, _defaultProjectRoot))
            throw new InvalidOperationException(
                "Work sessions move between Work projects; Chat has no target project.");

        string sourceChats = Path.Combine(source, "chats");
        string sourceSession = Path.GetFullPath(Path.Combine(sourceChats, sessionId));
        if (!IsUnder(sourceSession, sourceChats) || !Directory.Exists(sourceSession))
            throw new DirectoryNotFoundException($"Session '{sessionId}' was not found.");

        string targetChats = Path.Combine(target, "chats");
        Directory.CreateDirectory(targetChats);
        string destination = Path.GetFullPath(Path.Combine(targetChats, sessionId));
        if (!IsUnder(destination, targetChats))
            throw new InvalidOperationException("Session id leaves the target chats folder.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException(
                $"A session with id '{sessionId}' already exists in {target}.");

        Directory.Move(sourceSession, destination);

        if (SamePath(source, _defaultProjectRoot))
        {
            string chatFile = Path.Combine(destination, ChatSessionStore.ChatFileName);
            if (File.Exists(chatFile))
            {
                ChatSessionDocument document = JsonFile.Read<ChatSessionDocument>(chatFile);
                document = document with { Mode = "work" };
                JsonFile.Write(chatFile, document);
            }
        }

        NotifyProjectsChanged();
    }

    private string ValidateProjectPath(string path)
    {
        string full = Path.GetFullPath(path);
        bool isDefault = SamePath(full, _defaultProjectRoot);
        if (!isDefault && !IsUnder(full, StudioPaths.Workspaces))
            throw new InvalidOperationException("Only the default project or a Work project can be used.");
        if (!StudioProject.IsProject(full))
            throw new InvalidOperationException("No Studio project was found at that path.");
        return full;
    }

    private string ValidateWorkProject(string path)
    {
        string full = Path.GetFullPath(path);
        if (!IsUnder(full, StudioPaths.Workspaces) || !StudioProject.IsProject(full))
            throw new InvalidOperationException("Only a Work project can be archived or deleted.");
        if (SamePath(full, _defaultProjectRoot))
            throw new InvalidOperationException("The default project cannot be archived or deleted.");
        return full;
    }

    private static string ValidateArchive(string path)
    {
        string full = Path.GetFullPath(path);
        if (!IsUnder(full, StudioPaths.WorkspaceArchives) || !StudioProject.IsProject(full))
            throw new InvalidOperationException("Only an archived project can be restored or deleted.");
        return full;
    }

    private void NotifyProjectsChanged() => ProjectChanged?.Invoke();

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



