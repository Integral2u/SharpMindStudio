using SharpMind.Studio.Core.Runs;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class RunRetentionTests
{
    [Fact]
    public void FastestPerModel_KeepsTheHighestThroughput()
    {
        var older = Run("a-old", "a.smm", 10, 1);
        var faster = Run("a-fast", "a.smm", 30, 2);
        var other = Run("b-only", "b.smm", 5, 3);

        RunRetentionPlan plan = Assert.Single(
            RunRetention.Plan([older, faster, other], RunRetentionPolicy.FastestPerModel),
            item => item.ModelKey == "a.smm");

        Assert.Equal("a-fast", plan.Keep.RunId);
        Assert.Equal("a-old", Assert.Single(plan.Delete).RunId);
    }

    [Fact]
    public void LastPerModel_KeepsTheNewestEvenWhenSlower()
    {
        var olderFast = Run("a-old", "a.smm", 50, 1);
        var newestSlow = Run("a-new", "a.smm", 5, 2);

        RunRetentionPlan plan = Assert.Single(
            RunRetention.Plan([olderFast, newestSlow], RunRetentionPolicy.LastPerModel),
            item => item.ModelKey == "a.smm");

        Assert.Equal("a-new", plan.Keep.RunId);
        Assert.Equal("a-old", Assert.Single(plan.Delete).RunId);
    }

    [Fact]
    public void Plan_SeparatesModelsMissingAPathBySubject()
    {
        var first = Run("one", null, 10, 1, subject: "first.smm");
        var second = Run("two", null, 20, 2, subject: "second.smm");

        IReadOnlyList<RunRetentionPlan> plans = RunRetention.Plan(
            [first, second], RunRetentionPolicy.FastestPerModel);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Empty(plan.Delete));
    }

    private static RunSummary Run(
        string id,
        string? modelPath,
        double tps,
        int day,
        string? subject = null) =>
        new()
        {
            RunId = id,
            Kind = "model-bench",
            ModelPath = modelPath,
            Subject = subject,
            TokensPerSecond = tps,
            StartedUtc = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        };
}
