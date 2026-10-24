namespace SharpMind.Studio.Core.Recipes;

/// <summary>
/// The step-graph description of a build. A Recipe is intentionally data-only:
/// jobs execute it, the UI edits it, and the hash is what run history compares.
/// </summary>
public sealed record Recipe
{
    public const string CurrentSchema = "smmtx/1";

    public string Schema { get; init; } = CurrentSchema;
    public string Name { get; init; } = "";
    public PlaybookReference? Playbook { get; init; }
    public string? DeviceProfile { get; init; }
    public List<RecipeStep> Steps { get; init; } = [];
}
