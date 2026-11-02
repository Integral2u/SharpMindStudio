using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// One declarative planner rule. Conditions are ANDed; list values are ORed.
/// Rules only modify a plan skeleton (block a move, add a risk, choose a
/// playbook); the structural paths stay in code.
/// </summary>
public sealed class TrainingRule
{
    public string Id { get; set; } = "";
    public TrainingRuleCondition When { get; set; } = new();
    public TrainingRuleAction Then { get; set; } = new();
}

public sealed class TrainingRuleCondition
{
    /// <summary>Plan ids this rule applies to; null = all three.</summary>
    public List<string>? Plan { get; set; }

    public List<string>? Start { get; set; }
    public List<string>? Task { get; set; }
    public List<string>? Runtime { get; set; }
    public List<string>? Data { get; set; }
    public List<string>? Constraint { get; set; }
    public List<string>? Effort { get; set; }

    public bool? FromBase { get; set; }
    public bool? ArchitectureChange { get; set; }
    public bool? HasData { get; set; }
    public bool? HasGoodLooksLike { get; set; }

    public List<string>? GoalsAll { get; set; }
    public List<string>? GoalsAny { get; set; }
}

public sealed class TrainingRuleAction
{
    /// <summary>Move action names (Data, Pretrain, LoRA, Sft, ...) to block.</summary>
    public List<string>? Block { get; set; }

    public string? BlockedReason { get; set; }
    public string? Alternative { get; set; }
    public string? AddRisk { get; set; }
    public string? Playbook { get; set; }
}

/// <summary>
/// A versioned planner rule set. Loaded from Training/rules when present,
/// otherwise from the embedded default; plans record the id/version that
/// produced them.
/// </summary>
public sealed class TrainingRuleSet
{
    public string Id { get; set; } = "studio-training";
    public string Version { get; set; } = "1";
    public List<TrainingRule> Rules { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TrainingRuleSet Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        TrainingRuleSet set = JsonSerializer.Deserialize<TrainingRuleSet>(json, Json)
                              ?? throw new InvalidDataException("The planner rule set is empty.");
        set.Validate();
        return set;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidDataException("The planner rule set needs an id.");
        if (string.IsNullOrWhiteSpace(Version))
            throw new InvalidDataException("The planner rule set needs a version.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TrainingRule rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new InvalidDataException("Every planner rule needs an id.");
            if (!ids.Add(rule.Id))
                throw new InvalidDataException($"Planner rule '{rule.Id}' appears more than once.");

            bool hasAction = rule.Then.Block is { Count: > 0 }
                             || !string.IsNullOrWhiteSpace(rule.Then.AddRisk)
                             || !string.IsNullOrWhiteSpace(rule.Then.Playbook);
            if (!hasAction)
                throw new InvalidDataException($"Planner rule '{rule.Id}' has no action.");

            ValidateValues(rule, rule.When.Plan, ["fast", "balanced", "ambitious"], "plan");
            ValidateValues(rule, rule.When.Start, Enum.GetNames<PlannerStart>(), "start");
            ValidateValues(rule, rule.When.Task, Enum.GetNames<PlannerTask>(), "task");
            ValidateValues(rule, rule.When.Runtime, Enum.GetNames<PlannerRuntime>(), "runtime");
            ValidateValues(rule, rule.When.Data, Enum.GetNames<PlannerDataShape>(), "data");
            ValidateValues(rule, rule.When.Constraint, Enum.GetNames<PlannerConstraint>(), "constraint");
            ValidateValues(rule, rule.When.Effort, Enum.GetNames<PlannerEffort>(), "effort");
            ValidateValues(rule, rule.When.GoalsAll, Enum.GetNames<PlannerGoal>(), "goalsAll");
            ValidateValues(rule, rule.When.GoalsAny, Enum.GetNames<PlannerGoal>(), "goalsAny");
            ValidateValues(rule, rule.Then.Block, Enum.GetNames<PlannerAction>(), "block");
        }
    }

    private static void ValidateValues(
        TrainingRule rule, IReadOnlyList<string>? values, IReadOnlyCollection<string> allowed, string field)
    {
        if (values is null) return;
        foreach (string value in values)
        {
            if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Planner rule '{rule.Id}' has unknown {field} value '{value}'.");
        }
    }
}
