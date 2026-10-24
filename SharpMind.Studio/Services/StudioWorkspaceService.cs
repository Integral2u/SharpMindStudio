using Microsoft.Extensions.Options;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Projects;
using SharpMind.Studio.Core.Runs;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>The one project this local Studio instance is showing.</summary>
public sealed class StudioWorkspaceService
{
    public StudioWorkspaceService(IOptions<StudioOptions> options, IHostEnvironment environment)
    {
        StudioPaths.EnsureCreated();

        string configured = options.Value.ProjectRoot;
        string root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(StudioPaths.Root, configured);

        Project = StudioProject.IsProject(root)
            ? StudioProject.Open(root)
            : StudioProject.Create(root, "Studio");
        Runs = new RunStore(Project.RunsDirectory);
        ModelLists = new ModelListStore(Project.ModelListsDirectory);
        ModelsFolder = options.Value.ResolvedModelsFolder;
        CorpusFolder = options.Value.ResolvedCorpusFolder;
        TrainingFolder = options.Value.ResolvedTrainingFolder;
        PluginsFolder = options.Value.ResolvedPluginsFolder;
    }

    public StudioProject Project { get; }
    public RunStore Runs { get; }
    public ModelListStore ModelLists { get; }
    public string ModelsFolder { get; }
    public string CorpusFolder { get; }
    public string TrainingFolder { get; }
    public string PluginsFolder { get; }
    private BuilderCatalogResult? _builders;
    public BuilderCatalogResult Builders => _builders ??= BuilderCatalog.Discover(PluginsFolder);

    public Task<IReadOnlyList<RunSummary>> RecentRunsAsync(CancellationToken cancellationToken = default) =>
        RunCatalog.ListAsync(Runs, cancellationToken);

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
}



