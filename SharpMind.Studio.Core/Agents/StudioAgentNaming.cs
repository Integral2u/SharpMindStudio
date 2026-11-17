using SharpMind.Inference.Agent;

namespace SharpMind.Studio.Core.Agents;

/// <summary>
/// Keeps Studio's agent names on the same convention as the CUI: the active
/// sampling temperature selects a Greek tier (<c>Alpha</c> low to
/// <c>Epsilon</c> high), and a session without an explicit temperature keeps
/// the CUI default, <c>Delta</c>. Sub-agents created without a name still get
/// the <c>{Deity}-{Tier}</c> form from <see cref="GreekTier"/>.
/// </summary>
public static class StudioAgentNaming
{
    public const string Default = "Delta";

    public static string ForTemperature(float? temperature) =>
        temperature is { } value ? GreekTier.TierForTemperature(value) : Default;
}
