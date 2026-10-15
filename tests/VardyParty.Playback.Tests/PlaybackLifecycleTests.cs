using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackLifecycleTests
{
    private const PlaybackLifecycle Active = PlaybackLifecycle.Active;
    private const PlaybackLifecycle Suspended = PlaybackLifecycle.Suspended;
    private const PlaybackLifecycle Closing = PlaybackLifecycle.Closing;

    [Theory]
    [InlineData(PlaybackLifecycle.Active, false, PlaybackLifecycle.Suspended)]   // Home / TV system overlay
    [InlineData(PlaybackLifecycle.Suspended, false, PlaybackLifecycle.Suspended)]
    [InlineData(PlaybackLifecycle.Active, true, PlaybackLifecycle.Closing)]      // Back / finish
    [InlineData(PlaybackLifecycle.Suspended, true, PlaybackLifecycle.Closing)]
    [InlineData(PlaybackLifecycle.Closing, false, PlaybackLifecycle.Closing)]    // terminal
    [InlineData(PlaybackLifecycle.Closing, true, PlaybackLifecycle.Closing)]
    public void OnPause_SuspendsUnlessFinishing_AndClosingIsTerminal(
        PlaybackLifecycle current, bool isFinishing, PlaybackLifecycle expected)
    {
        Assert.Equal(expected, PlaybackLifecycleRules.OnPause(current, isFinishing));
    }

    [Theory]
    [InlineData(PlaybackLifecycle.Suspended, PlaybackLifecycle.Active)]
    [InlineData(PlaybackLifecycle.Active, PlaybackLifecycle.Active)]
    [InlineData(PlaybackLifecycle.Closing, PlaybackLifecycle.Closing)]
    public void OnResume_WakesOnlyASuspendedPlayer(PlaybackLifecycle current, PlaybackLifecycle expected)
    {
        Assert.Equal(expected, PlaybackLifecycleRules.OnResume(current));
    }

    [Theory]
    [InlineData(PlaybackLifecycle.Active, false)]
    [InlineData(PlaybackLifecycle.Suspended, true)]
    [InlineData(PlaybackLifecycle.Closing, true)]
    public void SuppressesWaitChrome_UnlessActive(PlaybackLifecycle lifecycle, bool expected)
    {
        Assert.Equal(expected, PlaybackLifecycleRules.SuppressesWaitChrome(lifecycle));
    }

    [Theory]
    [InlineData(PlaybackLifecycle.Suspended, true, true, ResumeAction.Reattach)]
    [InlineData(PlaybackLifecycle.Suspended, false, true, ResumeAction.None)]    // player gone: nothing to attach to
    [InlineData(PlaybackLifecycle.Suspended, true, false, ResumeAction.None)]    // no stream to attach
    [InlineData(PlaybackLifecycle.Active, true, true, ResumeAction.None)]        // first resume at startup
    [InlineData(PlaybackLifecycle.Closing, true, true, ResumeAction.None)]       // never revive a closing player
    public void OnResumeAction_ReattachesOnlyAfterASuspendWithAPlayerAndAStream(
        PlaybackLifecycle before, bool hasPlayer, bool hasStream, ResumeAction expected)
    {
        Assert.Equal(expected, PlaybackLifecycleRules.OnResumeAction(before, hasPlayer, hasStream));
    }

    [Fact]
    public void DeliberatePause_Stop_Idle_NeverShowsASpinner_AndNothingNeedsToExpireIt()
    {
        // The reported hole: Home -> OnPause -> Stop() -> IDLE with the listener attached and the watchdog
        // stopped left the spinner up forever. A suspended lifecycle suppresses the chrome up front.
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(new PlayerSnapshot(PlaybackStatusTracker.StateReady, 100, 5_000), 0, false, false);
        var lifecycle = PlaybackLifecycleRules.OnPause(Active, isFinishing: false);

        var idle = tracker.Assess(
            new PlayerSnapshot(PlaybackStatusTracker.StateIdle, 0, 5_000),
            100,
            isPreparing: false,
            chromeSuppressed: PlaybackLifecycleRules.SuppressesWaitChrome(lifecycle));
        var muchLater = tracker.Assess(
            new PlayerSnapshot(PlaybackStatusTracker.StateIdle, 0, 5_000),
            600_000,
            isPreparing: false,
            chromeSuppressed: PlaybackLifecycleRules.SuppressesWaitChrome(lifecycle));

        Assert.Equal(PlaybackStatus.Idle, idle.Status);
        Assert.False(idle.ShowSpinner);
        Assert.False(muchLater.ShowSpinner);
    }

    [Fact]
    public void Pause_Resume_Reattach_ShowsTheSpinnerOnlyWhilePreparing_ThenPlays()
    {
        var tracker = new PlaybackStatusTracker();
        var lifecycle = PlaybackLifecycleRules.OnPause(Active, isFinishing: false);
        tracker.Assess(new PlayerSnapshot(PlaybackStatusTracker.StateIdle, 0, 5_000), 100, false, true);

        var before = lifecycle;
        lifecycle = PlaybackLifecycleRules.OnResume(lifecycle);
        var action = PlaybackLifecycleRules.OnResumeAction(before, hasPlayer: true, hasStream: true);
        tracker.Reset(); // AttachEngine resets the tracker and marks the prepare in flight
        var preparing = tracker.Assess(
            new PlayerSnapshot(PlaybackStatusTracker.StateBuffering, 0, 0),
            1_000,
            isPreparing: true,
            chromeSuppressed: PlaybackLifecycleRules.SuppressesWaitChrome(lifecycle));
        var ready = tracker.Assess(
            new PlayerSnapshot(PlaybackStatusTracker.StateReady, 100, 0),
            1_500,
            isPreparing: false,
            chromeSuppressed: PlaybackLifecycleRules.SuppressesWaitChrome(lifecycle));

        Assert.Equal(Active, lifecycle);
        Assert.Equal(ResumeAction.Reattach, action);
        Assert.True(preparing.ShowSpinner);
        Assert.Equal(PlaybackStatus.Playing, ready.Status);
        Assert.False(ready.ShowSpinner);
    }

    [Fact]
    public void ClosingAfterSuspend_StaysClosing_AndNeverResumes()
    {
        var lifecycle = PlaybackLifecycleRules.OnPause(Active, isFinishing: false);
        Assert.Equal(Suspended, lifecycle);

        lifecycle = PlaybackLifecycleRules.OnPause(lifecycle, isFinishing: true);
        var resumed = PlaybackLifecycleRules.OnResume(lifecycle);

        Assert.Equal(Closing, lifecycle);
        Assert.Equal(Closing, resumed);
        Assert.Equal(ResumeAction.None, PlaybackLifecycleRules.OnResumeAction(lifecycle, true, true));
    }
}
