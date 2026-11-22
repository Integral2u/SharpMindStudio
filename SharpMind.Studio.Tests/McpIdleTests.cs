using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The idle sweep exists but nothing called it, so every server Studio started
/// stayed alive until the app exited and the per-server idle setting did
/// nothing. These use a manual clock, so they do not wait for a real timeout.
/// </summary>
public sealed class McpIdleTests
{
    [Fact]
    public async Task Host_StopsAServerAfterItsIdleTimeout()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new McpServerHost(time);
        string? stopped = null;
        host.Stopped += id => stopped = id;

        await host.EnsureStartedAsync(
            FakeMcpServer.Settings(idleSeconds: 5),
            () => Task.FromResult(true));
        Assert.True(host.IsRunning(FakeMcpServer.Id));

        await host.StopIdleAsync();
        Assert.True(host.IsRunning(FakeMcpServer.Id));

        time.Advance(TimeSpan.FromSeconds(6));
        await host.StopIdleAsync();

        Assert.False(host.IsRunning(FakeMcpServer.Id));
        Assert.Empty(host.Connected);
        Assert.Equal(FakeMcpServer.Id, stopped);
    }

    [Fact]
    public async Task Host_KeepsAServerWhenIdleIsDisabled()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new McpServerHost(time);

        await host.EnsureStartedAsync(
            FakeMcpServer.Settings(idleSeconds: 0),
            () => Task.FromResult(true));
        time.Advance(TimeSpan.FromDays(1));
        await host.StopIdleAsync();

        Assert.True(host.IsRunning(FakeMcpServer.Id));
    }

    [Fact]
    public async Task Host_AppliesAnUpdatedIdleTimeoutToARunningServer()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new McpServerHost(time);

        McpServerSettings settings = FakeMcpServer.Settings(idleSeconds: 300);
        await host.EnsureStartedAsync(settings, () => Task.FromResult(true));

        host.UpdateSettings(settings with { IdleSeconds = 5 });
        time.Advance(TimeSpan.FromSeconds(6));
        await host.StopIdleAsync();

        Assert.False(host.IsRunning(FakeMcpServer.Id));
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
