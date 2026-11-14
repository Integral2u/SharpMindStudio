namespace SharpMind.Studio.Core.Compute;

/// <summary>One saved Vultr host, flattened for inventory matching.</summary>
public sealed record VultrHostEntry(
    string ProviderId,
    string Name,
    string? InstanceId,
    double? HourlyCost);

/// <summary>The instances and plan prices read from one Vultr API key.</summary>
public sealed record VultrAccountInstances(
    string AccountProviderId,
    IReadOnlyList<VultrHostEntry> Hosts,
    IReadOnlyList<VultrInstance> Instances,
    IReadOnlyDictionary<string, double> Costs);

/// <summary>An account whose instance list could not be read.</summary>
public sealed record VultrAccountFailure(string AccountProviderId, string Message);

/// <summary>What the Compute page, the left menu and the exit prompt display.</summary>
public sealed record VultrComputeInstance(
    string AccountProviderId,
    string? OwnerProviderId,
    string OwnerName,
    string InstanceId,
    string Label,
    string Status,
    string PowerStatus,
    string? MainIp,
    string Region,
    string PlanId,
    double HourlyCost,
    DateTimeOffset? CreatedUtc)
{
    public bool IsRunning =>
        string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase)
        && string.Equals(PowerStatus, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the instance is ours but no saved host tracks it.</summary>
    public bool IsOrphan => OwnerProviderId is null;

    public string StatusLabel => IsOrphan
        ? (string.IsNullOrWhiteSpace(Status) ? "unknown (untracked)" : $"{Status} (untracked)")
        : string.IsNullOrWhiteSpace(Status) ? "unknown" : Status;

    public TimeSpan? Uptime =>
        CreatedUtc is { } created ? DateTimeOffset.UtcNow - created : null;

    public double EstimatedCost =>
        Uptime is { } up ? Math.Max(0, up.TotalHours) * HourlyCost : 0;
}

public sealed record VultrComputeInventory(
    IReadOnlyList<VultrComputeInstance> Instances,
    IReadOnlyList<string> MissingTrackedProviderIds,
    IReadOnlyList<VultrAccountFailure> Failures);

/// <summary>
/// Matches Vultr instances back to the saved host entries. Instances are listed
/// when a saved host tracks them or when they carry the <c>sharpmind</c> tag or
/// label prefix; everything else on the account is ignored. A tracked host whose
/// instance is gone is reported so its stale id can be cleared.
/// </summary>
public static class VultrComputeInventoryBuilder
{
    public const string Tag = "sharpmind";
    public const string LabelPrefix = "sharpmind-";

    public static VultrComputeInventory Build(
        IReadOnlyList<VultrAccountInstances> accounts,
        IReadOnlyList<VultrAccountFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var instances = new List<VultrComputeInstance>();
        var missing = new List<string>();

        foreach (VultrAccountInstances account in accounts)
        {
            foreach (VultrInstance instance in account.Instances)
            {
                VultrHostEntry? owner = account.Hosts.FirstOrDefault(host =>
                    string.Equals(host.InstanceId, instance.Id, StringComparison.Ordinal));

                if (!IsSharpMindInstance(instance) && owner is null) continue;

                double cost = account.Costs.TryGetValue(instance.Plan, out double hourly)
                    ? hourly
                    : owner?.HourlyCost ?? 0;

                instances.Add(new VultrComputeInstance(
                    owner?.ProviderId ?? account.AccountProviderId,
                    owner?.ProviderId,
                    owner?.Name ?? "Untracked instance",
                    instance.Id,
                    string.IsNullOrWhiteSpace(instance.Label) ? instance.Id : instance.Label,
                    instance.Status,
                    instance.PowerStatus,
                    instance.MainIp,
                    instance.Region,
                    instance.Plan,
                    cost,
                    instance.CreatedUtc));
            }

            foreach (VultrHostEntry host in account.Hosts)
            {
                if (string.IsNullOrWhiteSpace(host.InstanceId)) continue;
                if (account.Instances.Any(instance =>
                    string.Equals(instance.Id, host.InstanceId, StringComparison.Ordinal)))
                {
                    continue;
                }

                missing.Add(host.ProviderId);
            }
        }

        return new VultrComputeInventory(
            [.. instances
                .OrderByDescending(instance => instance.IsRunning)
                .ThenBy(instance => instance.Label, StringComparer.OrdinalIgnoreCase)],
            [.. missing],
            failures ?? []);
    }

    public static bool IsSharpMindInstance(VultrInstance instance) =>
        string.Equals(instance.Tag, Tag, StringComparison.OrdinalIgnoreCase)
        || instance.Label.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase);
}
