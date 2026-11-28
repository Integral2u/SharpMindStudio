namespace SharpMind.Studio.Core.Compute;

/// <summary>
/// One Akamai Cloud (Linode) instance this Studio created, shown on the Compute
/// page and in the left menu beside the Vultr rows.
/// </summary>
public sealed record AkamaiComputeInstance(
    string ProviderId,
    string OwnerName,
    string InstanceId,
    string Label,
    string Status,
    string? MainIp,
    string Region,
    string TypeId,
    double HourlyCost,
    DateTimeOffset? CreatedUtc)
{
    public bool IsRunning =>
        string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    public string StatusLabel =>
        string.IsNullOrWhiteSpace(Status) ? "unknown" : Status;

    public TimeSpan? Uptime =>
        CreatedUtc is { } created ? DateTimeOffset.UtcNow - created : null;

    public double EstimatedCost =>
        Uptime is { } up ? Math.Max(0, up.TotalHours) * HourlyCost : 0;
}
