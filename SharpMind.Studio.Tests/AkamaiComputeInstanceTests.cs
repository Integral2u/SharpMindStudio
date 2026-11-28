using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class AkamaiComputeInstanceTests
{
    [Fact]
    public void Activity_MatchesAnAkamaiProviderId()
    {
        Assert.Equal(
            "training",
            ComputeUsage.Activity("compute-akamai", "compute-akamai", true, null, false));
        Assert.Equal(
            "chat",
            ComputeUsage.Activity("compute-akamai", null, false, "compute-akamai", true));
        Assert.Null(
            ComputeUsage.Activity("compute-akamai", "compute-other", true, "compute-other", true));
    }

    [Fact]
    public void Instance_ReportsRunningOnlyForARunningLinode()
    {
        AkamaiComputeInstance instance = Instance("running");

        Assert.True(instance.IsRunning);
        Assert.Equal("running", instance.StatusLabel);
        Assert.False(Instance("booting").IsRunning);
    }

    [Fact]
    public void Instance_EstimatesSpendFromUptime()
    {
        AkamaiComputeInstance instance = Instance("running") with
        {
            HourlyCost = 1.5,
            CreatedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            Status = "running",
        };

        Assert.InRange(instance.EstimatedCost, 2.99, 3.01);
    }

    private static AkamaiComputeInstance Instance(string status) =>
        new(
            "compute-akamai",
            "Akamai Cloud",
            "112105794",
            "sharpmind-akamai",
            status,
            "172.105.8.31",
            "ca-central",
            "g6-nanode-1",
            0.0075,
            DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
}
