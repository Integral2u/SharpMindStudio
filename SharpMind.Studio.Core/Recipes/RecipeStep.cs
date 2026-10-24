using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.Recipes;

/// <summary>
/// One node in a Recipe graph. <see cref="Params"/> stays a loose JSON object
/// until a step type's schema is loaded; the graph itself only depends on the
/// stable id, type and dependencies.
/// </summary>
public sealed record RecipeStep
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public List<string> Needs { get; init; } = [];
    public string? Base { get; init; }
    public JsonObject? Params { get; init; }
}
