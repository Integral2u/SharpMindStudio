using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeIdleTrackerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observe_StartsCountdownAtTheTimeoutAndDestroysWhenItExpires()
    {
        var tracker = new ComputeIdleTracker();

        Assert.Equal(ComputeIdlePhase.Idle, tracker.Observe("host", 30, false, Start).Phase);
        Assert.Equal(
            ComputeIdlePhase.Idle,
            tracker.Observe("host", 30, false, Start.AddMinutes(29)).Phase);

        ComputeIdleObservation countdown = tracker.Observe(
            "host", 30, false, Start.AddMinutes(30));
        Assert.Equal(ComputeIdlePhase.Countdown, countdown.Phase);
        Assert.Equal(TimeSpan.FromMinutes(10), countdown.Remaining);

        Assert.Equal(
            ComputeIdlePhase.Countdown,
            tracker.Observe("host", 30, false, Start.AddMinutes(39)).Phase);
        Assert.Equal(
            ComputeIdlePhase.Destroy,
            tracker.Observe("host", 30, false, Start.AddMinutes(40)).Phase);
    }

    [Fact]
    public void Observe_ActiveWorkRestartsTheWindow()
    {
        var tracker = new ComputeIdleTracker();

        tracker.Observe("host", 30, false, Start);
        Assert.Equal(
            ComputeIdlePhase.None,
            tracker.Observe("host", 30, true, Start.AddMinutes(20)).Phase);
        Assert.Equal(
            ComputeIdlePhase.Idle,
            tracker.Observe("host", 30, false, Start.AddMinutes(20)).Phase);
        Assert.Equal(
            ComputeIdlePhase.Idle,
            tracker.Observe("host", 30, false, Start.AddMinutes(49)).Phase);
        Assert.Equal(
            ComputeIdlePhase.Countdown,
            tracker.Observe("host", 30, false, Start.AddMinutes(50)).Phase);
    }

    [Fact]
    public void Keep_ClearsTheCountdownAndStartsANewWindow()
    {
        var tracker = new ComputeIdleTracker();

        tracker.Observe("host", 30, false, Start);
        Assert.Equal(
            ComputeIdlePhase.Countdown,
            tracker.Observe("host", 30, false, Start.AddMinutes(30)).Phase);

        tracker.Keep("host", Start.AddMinutes(31));
        Assert.Equal(
            ComputeIdlePhase.Idle,
            tracker.Observe("host", 30, false, Start.AddMinutes(40)).Phase);
        Assert.Equal(
            ComputeIdlePhase.Countdown,
            tracker.Observe("host", 30, false, Start.AddMinutes(61)).Phase);
    }

    [Fact]
    public void Observe_NeverDoesNotStartACountdown()
    {
        var tracker = new ComputeIdleTracker();

        Assert.Equal(ComputeIdlePhase.None, tracker.Observe("host", 0, false, Start).Phase);
        Assert.Equal(
            ComputeIdlePhase.None,
            tracker.Observe("host", 0, false, Start.AddHours(2)).Phase);
    }
}
