using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Per-tool permission. <see cref="Off"/> removes the tool from the prompt
/// entirely, so the model never sees it and no tokens are spent advertising
/// it. <see cref="Never"/> keeps the tool visible but refuses every call
/// without prompting. <see cref="Ask"/> prompts, and <see cref="Always"/>
/// permits without asking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkToolPermission>))]
public enum WorkToolPermission
{
    Off,
    Never,
    Ask,
    Always,
}

/// <summary>
/// One layer of tool permissions: an override for a named tool, or for every
/// tool in a group. Empty layers inherit, which is why every member is a
/// dictionary rather than a value that would need a sentinel.
/// </summary>
public sealed record ToolPermissionSet
{
    public Dictionary<string, WorkToolPermission> Tools { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WorkToolPermission> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Tools.Count == 0 && Groups.Count == 0;

    /// <summary>
    /// Looks a tool up by name. Falls back to a case-insensitive scan because
    /// a round-tripped JSON dictionary loses the comparer used to build it.
    /// </summary>
    public bool TryTool(string name, out WorkToolPermission value)
    {
        if (Tools.TryGetValue(name, out value)) return true;
        foreach (KeyValuePair<string, WorkToolPermission> pair in Tools)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Same case-insensitive fallback as <see cref="TryTool"/>.</summary>
    public bool TryGroup(string group, out WorkToolPermission value)
    {
        if (Groups.TryGetValue(group, out value)) return true;
        foreach (KeyValuePair<string, WorkToolPermission> pair in Groups)
        {
            if (!string.Equals(pair.Key, group, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = default;
        return false;
    }
}

/// <summary>A resolved tool permission and the layer that supplied it.</summary>
public readonly record struct ToolPermissionValue(
    WorkToolPermission Permission,
    string Source,
    bool Escalates = false)
{
    /// <summary>True when the tool is advertised to the model.</summary>
    public bool IsVisible => Permission != WorkToolPermission.Off;
}

/// <summary>
/// The tool permission layers for one run, in precedence order. Each layer is
/// optional; an absent layer inherits from the one beneath it.
/// </summary>
public sealed record ToolPermissionProfile
{
    public ToolPermissionSet? Agent { get; init; }
    public ToolPermissionSet? Session { get; init; }
    public ToolPermissionSet? Model { get; init; }
    public ToolPermissionSet? Family { get; init; }
    public ToolPermissionSet? Project { get; init; }
    public ToolPermissionSet? System { get; init; }

    /// <summary>Used when no layer mentions a tool; new plugin tools should pass <see cref="WorkToolPermission.Off"/>.</summary>
    public WorkToolPermission Fallback { get; init; } = WorkToolPermission.Ask;

    public ToolPermissionValue For(string tool, string? group = null) =>
        ToolPermissionResolver.Resolve(
            tool, group, System, Family, Project, Model, Session, Agent, Fallback);

    /// <summary>The chain without the agent layer, for showing what an agent is escalating over.</summary>
    public WorkToolPermission InheritedFor(string tool, string? group = null) =>
        ToolPermissionResolver.ResolveWithoutAgent(
            tool, group, System, Family, Project, Model, Session, Fallback);
}

/// <summary>
/// Resolves tool permissions through the override chain and applies the
/// folder permission as a cap.
/// </summary>
/// <remarks>
/// Layer precedence is agent, session, model, family, project, system. The
/// first layer with an entry wins outright, so a session group override beats
/// a model per-tool override. Within one layer a per-tool entry beats the group
/// entry. A sparse layer inherits from the layer beneath it.
///
/// The project layer carries the existing per-project tool toggles and the
/// system layer carries the Studio defaults, so neither had to be re-expressed
/// when the four-state permissions arrived.
///
/// The agent layer is special: an agent may ask for more than the session
/// allows, but that never takes effect silently — the resolved value carries
/// <see cref="ToolPermissionValue.Escalates"/> so the caller can prompt the
/// user instead of widening the session policy.
/// </remarks>
public static class ToolPermissionResolver
{
    public const string DefaultSource = "Default";

    public static ToolPermissionValue Resolve(
        string tool,
        string? group = null,
        ToolPermissionSet? system = null,
        ToolPermissionSet? family = null,
        ToolPermissionSet? project = null,
        ToolPermissionSet? model = null,
        ToolPermissionSet? session = null,
        ToolPermissionSet? agent = null,
        WorkToolPermission fallback = WorkToolPermission.Ask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (TryLayer(agent, tool, group, out WorkToolPermission agentValue))
        {
            WorkToolPermission baseline = ResolveWithoutAgent(
                tool, group, system, family, project, model, session, fallback);
            return new ToolPermissionValue(
                agentValue,
                "Agent",
                Escalates: Rank(agentValue) > Rank(baseline));
        }

        if (TryLayer(session, tool, group, out WorkToolPermission sessionValue))
            return new ToolPermissionValue(sessionValue, "Session");
        if (TryLayer(model, tool, group, out WorkToolPermission modelValue))
            return new ToolPermissionValue(modelValue, "Model");
        if (TryLayer(family, tool, group, out WorkToolPermission familyValue))
            return new ToolPermissionValue(familyValue, "Family");
        if (TryLayer(project, tool, group, out WorkToolPermission projectValue))
            return new ToolPermissionValue(projectValue, "Project");
        if (TryLayer(system, tool, group, out WorkToolPermission systemValue))
            return new ToolPermissionValue(systemValue, "System");

        return new ToolPermissionValue(fallback, DefaultSource);
    }

    /// <summary>
    /// The chain without the agent layer. This is what an agent escalation is
    /// measured against, and what the settings panel shows as the inherited
    /// value at the point where an agent takes over.
    /// </summary>
    public static WorkToolPermission ResolveWithoutAgent(
        string tool,
        string? group = null,
        ToolPermissionSet? system = null,
        ToolPermissionSet? family = null,
        ToolPermissionSet? project = null,
        ToolPermissionSet? model = null,
        ToolPermissionSet? session = null,
        WorkToolPermission fallback = WorkToolPermission.Ask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (TryLayer(session, tool, group, out WorkToolPermission sessionValue))
            return sessionValue;
        if (TryLayer(model, tool, group, out WorkToolPermission modelValue))
            return modelValue;
        if (TryLayer(family, tool, group, out WorkToolPermission familyValue))
            return familyValue;
        if (TryLayer(project, tool, group, out WorkToolPermission projectValue))
            return projectValue;
        if (TryLayer(system, tool, group, out WorkToolPermission systemValue))
            return systemValue;
        return fallback;
    }

    /// <summary>
    /// Applies a folder permission. A folder set to <see cref="WorkPermission.Never"/>
    /// is a hard deny; otherwise the tool permission decides, so an explicit
    /// Always is not silently downgraded by a folder that is merely Ask.
    /// </summary>
    public static WorkToolPermission ApplyFolder(WorkToolPermission tool, WorkPermission folder) =>
        folder == WorkPermission.Never ? WorkToolPermission.Never : tool;

    /// <summary>The more restrictive of two permissions, using the enum order.</summary>
    public static WorkToolPermission MostRestrictive(WorkToolPermission left, WorkToolPermission right) =>
        Rank(left) <= Rank(right) ? left : right;

    public static WorkPermission ToWorkPermission(WorkToolPermission permission) => permission switch
    {
        WorkToolPermission.Never => WorkPermission.Never,
        WorkToolPermission.Always => WorkPermission.Always,
        _ => WorkPermission.Ask,
    };

    public static WorkToolPermission FromWorkPermission(WorkPermission permission) => permission switch
    {
        WorkPermission.Never => WorkToolPermission.Never,
        WorkPermission.Ask => WorkToolPermission.Ask,
        _ => WorkToolPermission.Always,
    };

    /// <summary>Off (0) &lt; Never (1) &lt; Ask (2) &lt; Always (3).</summary>
    public static int Rank(WorkToolPermission permission) => (int)permission;

    private static bool TryLayer(
        ToolPermissionSet? layer,
        string tool,
        string? group,
        out WorkToolPermission value)
    {
        value = default;
        if (layer is null) return false;
        if (layer.TryTool(tool, out value)) return true;
        if (!string.IsNullOrWhiteSpace(group) && layer.TryGroup(group, out value)) return true;
        return false;
    }
}
