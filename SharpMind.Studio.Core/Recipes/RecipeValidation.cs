using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Recipes;

public sealed record RecipeIssue(string Code, string Message, string? StepId = null);

public sealed record RecipeValidationResult(bool IsValid, IReadOnlyList<RecipeIssue> Issues)
{
    public static RecipeValidationResult Valid { get; } = new(true, []);
}

/// <summary>
/// Structural checks only: schema, ids, dependencies and cycles. Step-specific
/// parameter validation belongs to the step registry that will sit on top.
/// </summary>
public static partial class RecipeValidator
{
    [GeneratedRegex("^[a-z][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();

    public static RecipeValidationResult Validate(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var issues = new List<RecipeIssue>();
        if (recipe.Schema != Recipe.CurrentSchema)
            issues.Add(new("schema", $"Expected schema '{Recipe.CurrentSchema}', got '{recipe.Schema}'."));
        if (string.IsNullOrWhiteSpace(recipe.Name))
            issues.Add(new("name", "Recipe name is required."));
        if (recipe.Playbook is { } playbook && string.IsNullOrWhiteSpace(playbook.Id))
            issues.Add(new("playbook", "Playbook id is required when a playbook is referenced."));

        if (recipe.Steps.Count == 0)
        {
            issues.Add(new("steps", "A Recipe needs at least one step."));
            return new(false, issues);
        }

        var byId = new Dictionary<string, RecipeStep>(StringComparer.Ordinal);
        foreach (var step in recipe.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                issues.Add(new("step.id", "Every step needs an id."));
                continue;
            }
            if (!StepIdPattern().IsMatch(step.Id))
            {
                issues.Add(new("step.id", "Step ids use lower-case letters, digits, '.', '_' or '-'.", step.Id));
            }
            if (!byId.TryAdd(step.Id, step))
            {
                issues.Add(new("step.duplicate", $"Step id '{step.Id}' is used more than once.", step.Id));
            }
            if (string.IsNullOrWhiteSpace(step.Type))
                issues.Add(new("step.type", "Every step needs a type.", step.Id));

            var seenNeeds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string need in step.Needs)
            {
                if (need == step.Id)
                    issues.Add(new("step.self", $"Step '{step.Id}' cannot depend on itself.", step.Id));
                else if (!seenNeeds.Add(need))
                    issues.Add(new("step.needs", $"Step '{step.Id}' lists dependency '{need}' twice.", step.Id));
            }
        }

        foreach (var step in recipe.Steps)
        {
            foreach (string need in step.Needs)
            {
                if (!byId.ContainsKey(need) && need != step.Id)
                    issues.Add(new("step.missing", $"Step '{step.Id}' depends on unknown step '{need}'.", step.Id));
            }
        }

        if (issues.All(i => i.Code != "step.duplicate" && i.Code != "step.missing"))
            DetectCycles(byId, issues);

        return new(issues.Count == 0, issues);
    }

    private static void DetectCycles(Dictionary<string, RecipeStep> byId, List<RecipeIssue> issues)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string id in byId.Keys)
            Visit(id);

        void Visit(string id)
        {
            if (state.TryGetValue(id, out int value))
            {
                if (value == 1)
                    issues.Add(new("step.cycle", $"Dependency cycle includes step '{id}'.", id));
                return;
            }

            state[id] = 1;
            if (byId.TryGetValue(id, out var step))
            {
                foreach (string need in step.Needs)
                    if (byId.ContainsKey(need)) Visit(need);
            }
            state[id] = 2;
        }
    }
}
