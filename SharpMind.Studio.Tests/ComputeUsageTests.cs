using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeUsageTests
{
    [Fact]
    public void Activity_ReportsTrainingOnTheSameHost()
    {
        Assert.Equal(
            "training",
            ComputeUsage.Activity(Instance("compute-abc"), "compute-abc", true, null, false));
    }

    [Fact]
    public void Activity_ReportsChatWhileTheTurnIsBusy()
    {
        Assert.Equal(
            "chat",
            ComputeUsage.Activity(Instance("compute-abc"), null, false, "compute-abc", true));
    }

    [Fact]
    public void Activity_IgnoresWorkOnAnotherHost()
    {
        Assert.Null(
            ComputeUsage.Activity(Instance("compute-abc"), "compute-other", true, null, false));
        Assert.Null(
            ComputeUsage.Activity(Instance("compute-abc"), null, false, "compute-other", true));
    }

    [Fact]
    public void Activity_IgnoresIdleServices()
    {
        Assert.Null(
            ComputeUsage.Activity(Instance("compute-abc"), "compute-abc", false, "compute-abc", false));
    }

    [Fact]
    public void Activity_IgnoresUntrackedInstances()
    {
        VultrComputeInstance orphan = Instance("compute-abc") with { OwnerProviderId = null };

        Assert.Null(ComputeUsage.Activity(orphan, "compute-abc", true, "compute-abc", true));
    }

    private static VultrComputeInstance Instance(string providerId) =>
        new(
            providerId,
            providerId,
            "Vultr GPU",
            "instance-1",
            "sharpmind-abc",
            "active",
            "running",
            "203.0.113.10",
            "ewr",
            "vcg-a16",
            0.47,
            DateTimeOffset.UtcNow);
}
