using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Studio.Core.Safety;

namespace SharpMind.Studio.Services;

/// <summary>
/// Studio's gate in front of the engine's tool dispatch. It runs for local,
/// remote and compute turns because all three reach their tools through
/// <see cref="AgentBuilder.CallToolAsync"/>.
/// </summary>
public sealed class StudioToolCallFilter(
    StudioSafetyService safety,
    string conversation) : IToolCallFilter
{
    public async Task<string?> ReviewAsync(
        string tool,
        JsonObject arguments,
        CancellationToken cancellationToken = default)
    {
        if (!safety.Enabled) return null;

        IReadOnlyList<SafetyFinding> findings = ToolCallInspector.Inspect(SafetyRequest.For(
            tool,
            arguments.ToJsonString(),
            conversation,
            allowedHosts: safety.AllowedHosts));

        SafetyFinding? blocking = findings.FirstOrDefault(
            finding => ToolCallInspector.Blocks(finding, safety.Threshold));
        if (blocking is null) return null;

        SafetyDecision decision = await safety
            .ReviewAsync(tool, blocking, cancellationToken)
            .ConfigureAwait(false);

        return decision is SafetyDecision.Deny or SafetyDecision.DenyAlways
            ? $"Studio blocked this call: {blocking.Message}"
            : null;
    }
}
