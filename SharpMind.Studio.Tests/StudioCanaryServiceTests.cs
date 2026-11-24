using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioCanaryServiceTests
{
    [Fact]
    public async Task Clear_EmptiesTheCurrentCanaryRun()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"sharp-mind-canary-{Guid.NewGuid():n}");
        try
        {
            var service = new StudioCanaryService(new StudioActivityService());
            var request = new CanaryRequest
            {
                Models = [Path.Combine(root, "missing.smm")],
                WarmupTokens = 1,
                MeasureTokens = 1,
                FullLoad = false,
            };

            await service.StartAsync(request, new RunStore(root));
            await service.Current;

            var item = Assert.Single(service.Items);
            Assert.Equal("failed", item.Status);
            Assert.False(service.IsRunning);

            service.Clear();

            Assert.Empty(service.Items);
            Assert.Empty(service.ModelPaths);
            Assert.Empty(service.Results);
            Assert.False(service.CanContinue);
            Assert.Equal("Ready.", service.Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SkipCurrent_IsNotAvailableWhileIdle()
    {
        var service = new StudioCanaryService(new StudioActivityService());

        Assert.False(service.CanSkip);
        service.SkipCurrent();
        Assert.False(service.CanSkip);
        Assert.Equal("Ready.", service.Status);
    }
}
