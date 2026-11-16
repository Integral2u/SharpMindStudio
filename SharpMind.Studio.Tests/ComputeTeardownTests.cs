using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeTeardownTests
{
    [Fact]
    public void IsOwned_OnlyAcceptsATrackedHostEntry()
    {
        Assert.True(ComputeTeardown.IsOwned(Instance("compute-host")));
        Assert.False(ComputeTeardown.IsOwned(Instance(null)));
    }

    private static VultrComputeInstance Instance(string? owner) =>
        new(
            "account",
            owner,
            owner ?? "untracked",
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
