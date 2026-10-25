using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

public sealed class ModelCatalogService
{
    private readonly ModelCatalog _catalog;
    private IReadOnlyList<ModelDescriptor> _models = [];

    public ModelCatalogService(StudioWorkspaceService workspace)
    {
        _catalog = new ModelCatalog(workspace.ModelsFolder);
    }

    public IReadOnlyList<ModelDescriptor> Models => _models;

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
