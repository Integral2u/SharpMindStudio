using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class VultrComputeInventoryTests
{
    [Fact]
    public void Build_MatchesATrackedInstanceAndUsesThePlanPrice()
    {
        VultrHostEntry host = new("compute-abc", "Vultr GPU", "instance-1", 0.50);
        var account = new VultrAccountInstances(
            host.ProviderId,
            [host],
            [Instance("instance-1", "sharpmind-abc", plan: "vcg-a16", tag: null)],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["vcg-a16"] = 0.47,
            });

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([account]);

        VultrComputeInstance instance = Assert.Single(inventory.Instances);
        Assert.Equal("compute-abc", instance.OwnerProviderId);
        Assert.Equal("Vultr GPU", instance.OwnerName);
        Assert.False(instance.IsOrphan);
        Assert.True(instance.IsRunning);
        Assert.Equal(0.47, instance.HourlyCost, 2);
        Assert.Empty(inventory.MissingTrackedProviderIds);
        Assert.Empty(inventory.Failures);
    }

    [Fact]
    public void Build_SurfacesATaggedInstanceWithNoSavedHost()
    {
        VultrHostEntry host = new("compute-abc", "Vultr GPU", null, null);
        var account = new VultrAccountInstances(
            host.ProviderId,
            [host],
            [Instance("orphan-1", "sharpmind-old", plan: "vc2-1c-1gb", tag: "sharpmind")],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([account]);

        VultrComputeInstance instance = Assert.Single(inventory.Instances);
        Assert.True(instance.IsOrphan);
        Assert.Null(instance.OwnerProviderId);
        Assert.Equal("Untracked instance", instance.OwnerName);
        Assert.Contains("untracked", instance.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_IgnoresUnrelatedInstancesOnTheAccount()
    {
        VultrHostEntry host = new("compute-abc", "Vultr GPU", null, null);
        var account = new VultrAccountInstances(
            host.ProviderId,
            [host],
            [
                Instance("other-1", "web-server", plan: "vc2-1c-1gb", tag: null),
                Instance("orphan-1", "sharpmind-old", plan: "vc2-1c-1gb", tag: null),
            ],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([account]);

        VultrComputeInstance instance = Assert.Single(inventory.Instances);
        Assert.Equal("orphan-1", instance.InstanceId);
    }

    [Fact]
    public void Build_ReportsATrackedInstanceThatIsGone()
    {
        VultrHostEntry host = new("compute-abc", "Vultr GPU", "destroyed-1", 0.47);
        var account = new VultrAccountInstances(
            host.ProviderId,
            [host],
            [],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([account]);

        Assert.Empty(inventory.Instances);
        Assert.Equal("compute-abc", Assert.Single(inventory.MissingTrackedProviderIds));
    }

    [Fact]
    public void Build_FallsBackToTheSavedHourlyCostWhenThePlanIsUnknown()
    {
        VultrHostEntry host = new("compute-abc", "Vultr GPU", "instance-1", 0.75);
        var account = new VultrAccountInstances(
            host.ProviderId,
            [host],
            [Instance("instance-1", "sharpmind-abc", plan: "gone-plan", tag: null)],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([account]);

        Assert.Equal(0.75, Assert.Single(inventory.Instances).HourlyCost, 2);
    }

    [Fact]
    public void Build_KeepsAccountFailuresForTheStatusLine()
    {
        var failure = new VultrAccountFailure("compute-abc", "Invalid API key.");

        VultrComputeInventory inventory = VultrComputeInventoryBuilder.Build([], [failure]);

        Assert.Empty(inventory.Instances);
        Assert.Equal("Invalid API key.", Assert.Single(inventory.Failures).Message);
    }

    private static VultrInstance Instance(
        string id,
        string label,
        string plan,
        string? tag) =>
        new(
            id,
            label,
            "active",
            "203.0.113.10",
            "running",
            "ok",
            "ewr",
            plan,
            0.12,
            new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
            tag);
}
