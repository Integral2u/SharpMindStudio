using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

public sealed class ModelCatalogService
{
    private readonly ModelCatalog _catalog;
    private readonly string _folder;
    private IReadOnlyList<ModelDescriptor> _models = [];

    public ModelCatalogService(StudioWorkspaceService workspace)
    {
        _folder = Path.GetFullPath(workspace.ModelsFolder);
        _catalog = new ModelCatalog(_folder);
    }

    public string Folder => _folder;

    public IReadOnlyList<ModelDescriptor> Models => _models;

    public bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full = Path.GetFullPath(path);
        return _models.Any(model => model.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies a finished .smm/.gguf into the Models folder so Chat and the labs
    /// can see it, then refreshes the catalog. <paramref name="fileName"/> gives
    /// a checkpoint export a unique name instead of its generic <c>model.smm</c>.
    /// </summary>
    public async Task<string> AddAsync(
        string sourcePath,
        string? fileName = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The model file was not found.", source);

        string extension = Path.GetExtension(source);
        if (!extension.Equals(".smm", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .smm and .gguf files can be added to the model list.");
        }

        string name = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(source)
            : Path.GetFileName(fileName);
        Directory.CreateDirectory(_folder);
        string destination = Path.Combine(_folder, name);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destination) && !overwrite)
            {
                throw new IOException(
                    $"{Path.GetFileName(destination)} is already in the model list.");
            }

            File.Copy(source, destination, overwrite);
        }

        await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public IReadOnlyList<ModelFamily> Families => [.. _models
        .Select(model => model.Family)
        .DistinctBy(family => family.Id)
        .OrderBy(family => family.DisplayName, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<ModelDescriptor>> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        _models = await _catalog.GetAsync(force, cancellationToken);
        return _models;
    }

    public ModelDescriptor? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full = Path.GetFullPath(path);
        ModelDescriptor? found = _models.FirstOrDefault(model =>
            model.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return found;

        try { return ModelCatalog.Describe(full); }
        catch { return null; }
    }

    public ModelFamily? FindFamily(string? id) =>
        Families.FirstOrDefault(family => family.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
