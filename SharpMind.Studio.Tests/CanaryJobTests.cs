using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Tests;

public sealed class CanaryJobTests
{
    [Fact]
    public async Task MissingModel_IsRecordedAsAFailedRun()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        string missing = temp.Combine("missing.gguf");

        var results = await CanaryJob.RunAsync(new CanaryRequest
        {
            Models = [missing],
            WarmupTokens = 1,
            MeasureTokens = 1,
        }, store);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));

        var summary = await RunCatalog.LoadAsync(store.Open(result.RunId));
        Assert.Equal("failed", summary.Status);
        Assert.Contains("FileNotFoundException", summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerModelCallback_FiresAsEachModelFinishes()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        var seen = new List<CanaryResult>();

        var results = await CanaryJob.RunAsync(new CanaryRequest
        {
            Models = [temp.Combine("missing-1.gguf"), temp.Combine("missing-2.gguf")],
            WarmupTokens = 1,
            MeasureTokens = 1,
        }, store, onResult: seen.Add);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, result => Assert.False(result.Succeeded));
    }
}
