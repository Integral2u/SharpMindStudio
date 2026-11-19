using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Per-tool permission. <see cref="Off"/> removes the tool from the prompt
/// entirely, so the model never sees it and no tokens are spent advertising
/// it. <see cref="Never"/> keeps the tool visible but refuses every call
/// without prompting. <see cref="Ask"/> prompts, and <see cref="Always"/>
/// permits without asking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolPermission>))]
public enum ToolPermission
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
    public Dictionary<string, ToolPermission> Tools { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ToolPermission> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Tools.Count == 0 && Groups.Count == 0;

    /// <summary>
    /// Looks a tool up by name. Falls back to a case-insensitive scan because
    /// a round-tripped JSON dictionary loses the comparer used to build it.
    /// </summary>
    public bool TryTool(string name, out ToolPermission value)
    {
        if (Tools.TryGetValue(name, out value)) return true;
        foreach (KeyValuePair<string, ToolPermission> pair in Tools)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Same case-insensitive fallback as <see cref="TryTool"/>.</summary>
    public bool TryGroup(string group, out ToolPermission value)
    {
        if (Groups.TryGetValue(group, out value)) return true;
        foreach (KeyValuePair<string, ToolPermission> pair in Groups)
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
    ToolPermission Permission,
    string Source,
    bool Escalates = false)
{
    /// <summary>True when the tool is advertised to the model.</summary>
    public bool IsVisible => Permission != ToolPermission.Off;
}

/// <summary>
/// Resolves tool permissions through the override chain and applies the
/// folder permission as a cap.
/// </summary>
/// <remarks>
/// Layer precedence is agent, session, model, family, system. The first layer
/// with an entry wins outright, so a session group override beats a model
/// per-tool override. Within one layer a per-tool entry beats the group entry.
/// A sparse layer inherits from the layer beneath it.
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
        ToolPermissionSet? model = null,
        ToolPermissionSet? session = null,
        ToolPermissionSet? agent = null,
        ToolPermission fallback = ToolPermission.Ask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (TryLayer(agent, tool, group, out ToolPermission agentValue))
        {
            ToolPermission baseline = ResolveWithoutAgent(
                tool, group, system, family, model, session, fallback);
            return new ToolPermissionValue(
                agentValue,
                "Agent",
                Escalates: Rank(agentValue) > Rank(baseline));
        }

        if (TryLayer(session, tool, group, out ToolPermission sessionValue))
            return new ToolPermissionValue(sessionValue, "Session");
        if (TryLayer(model, tool, group, out ToolPermission modelValue))
            return new ToolPermissionValue(modelValue, "Model");
        if (TryLayer(family, tool, group, out ToolPermission familyValue))
            return new ToolPermissionValue(familyValue, "Family");
        if (TryLayer(system, tool, group, out ToolPermission systemValue))
            return new ToolPermissionValue(systemValue, "System");

        return new ToolPermissionValue(fallback, DefaultSource);
    }

    /// <summary>
    /// The chain without the agent layer. This is what an agent escalation is
    /// measured against, and what the settings panel shows as the inherited
    /// value at the point where an agent takes over.
    /// </summary>
    public static ToolPermission ResolveWithoutAgent(
        string tool,
        string? group = null,
        ToolPermissionSet? system = null,
        ToolPermissionSet? family = null,
        ToolPermissionSet? model = null,
        ToolPermissionSet? session = null,
        ToolPermission fallback = ToolPermission.Ask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (TryLayer(session, tool, group, out ToolPermission sessionValue))
            return sessionValue;
        if (TryLayer(model, tool, group, out ToolPermission modelValue))
            return modelValue;
        if (TryLayer(family, tool, group, out ToolPermission familyValue))
            return familyValue;
        if (TryLayer(system, tool, group, out ToolPermission systemValue))
            return systemValue;
        return fallback;
    }

    /// <summary>
    /// Applies a folder permission as a cap: the more restrictive of the two
    /// wins. A tool set to <see cref="ToolPermission.Always"/> still refuses in
    /// a folder whose permission is <see cref="WorkPermission.Never"/>, so the
    /// folder stays a hard boundary.
    /// </summary>
    public static ToolPermission Cap(ToolPermission tool, WorkPermission folder) =>
        MostRestrictive(tool, FromWorkPermission(folder));

    /// <summary>The more restrictive of two permissions, using the enum order.</summary>
    public static ToolPermission MostRestrictive(ToolPermission left, ToolPermission right) =>
        Rank(left) <= Rank(right) ? left : right;

    public static WorkPermission ToWorkPermission(ToolPermission permission) => permission switch
    {
        ToolPermission.Never => WorkPermission.Never,
        ToolPermission.Always => WorkPermission.Always,
        _ => WorkPermission.Ask,
    };

    public static ToolPermission FromWorkPermission(WorkPermission permission) => permission switch
    {
        WorkPermission.Never => ToolPermission.Never,
        WorkPermission.Ask => ToolPermission.Ask,
        _ => ToolPermission.Always,
    };

    /// <summary>Off (0) &lt; Never (1) &lt; Ask (2) &lt; Always (3).</summary>
    public static int Rank(ToolPermission permission) => (int)permission;

    private static bool TryLayer(
        ToolPermissionSet? layer,
        string tool,
        string? group,
        out ToolPermission value)
    {
        value = default;
        if (layer is null) return false;
        if (layer.TryTool(tool, out value)) return true;
        if (!string.IsNullOrWhiteSpace(group) && layer.TryGroup(group, out value)) return true;
        return false;
    }
}
