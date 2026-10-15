using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackStatusTrackerTests
{
    private const int Idle = PlaybackStatusTracker.StateIdle;
    private const int Buffering = PlaybackStatusTracker.StateBuffering;
    private const int Ready = PlaybackStatusTracker.StateReady;
    private const int Ended = PlaybackStatusTracker.StateEnded;

    private static PlayerSnapshot Snap(int state, long pos = 0, int buffered = 0, bool error = false)
        => new(state, buffered, pos, error);

    // ---- verdict ---------------------------------------------------------------------------------

    [Fact]
    public void Ready_IsPlaying_EvenWhenPlayerIsStillLoadingMoreSegments()
    {
        // Regression: onIsLoadingChanged(true) while READY used to latch "Buffering" + spinner.
        // isLoading is not part of the snapshot, so a READY player can only be Playing.
        var tracker = new PlaybackStatusTracker();

        var status = tracker.Evaluate(Snap(Ready, pos: 5000, buffered: 100), nowMs: 1000);

        Assert.Equal(PlaybackStatus.Playing, status);
    }

    [Fact]
    public void Buffering_WithNoProgress_IsBuffering()
    {
        var tracker = new PlaybackStatusTracker();

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 1000), 0));
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 1000), 500));
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 1000), 10_000));
    }

    [Fact]
    public void Buffering_WithPositionAdvancingAtPlaybackRate_IsPlaying()
    {
        // Stale STATE_BUFFERING (event race / missed READY) while the clock is moving.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 1000), 0);

        var status = tracker.Evaluate(Snap(Buffering, pos: 1500), 500);

        Assert.Equal(PlaybackStatus.Playing, status);
    }

    [Fact]
    public void Buffering_FullBufferButFrozenPosition_StillBuffers()
    {
        // 100% buffered alone is not proof of playback: a frozen player must not be reported as playing.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 4000, buffered: 100), 0);

        var status = tracker.Evaluate(Snap(Buffering, pos: 4000, buffered: 100), 1000);

        Assert.Equal(PlaybackStatus.Buffering, status);
    }

    [Fact]
    public void Buffering_ClearsAfterProgressStops_OnlyOnceGraceExpires()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 1000), 0);
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 1400), 400));

        // Position stops moving: still treated as playing inside the grace window...
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 1400), 400 + PlaybackStatusTracker.ProgressGraceMs));
        // ...and genuinely buffering after it.
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 1400), 401 + PlaybackStatusTracker.ProgressGraceMs));
    }

    [Fact]
    public void EventOrdering_BufferingThenReady_ThenLateBufferingEvent_StaysPlayingWhileAdvancing()
    {
        var tracker = new PlaybackStatusTracker();
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 0), 0));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready, pos: 0), 100));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready, pos: 600), 700));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 1200), 1300));
    }

    [Fact]
    public void StuckSpinner_Watchdog_RecoversOnNextTickWithoutAnyPlayerEvent()
    {
        // The state is stuck at BUFFERING, no further events arrive, but the clock is moving:
        // the periodic tick alone must clear it.
        var tracker = new PlaybackStatusTracker();
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 100), 0));

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 100), 500));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 600), 1000));
    }

    // ---- position heuristic: what is NOT progress -----------------------------------------------

    [Fact]
    public void SeekBackwards_DoesNotCountAsProgress()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 9000), 0);

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 2000), 500));
    }

    [Fact]
    public void ForwardJumpWhileBuffering_LiveEdgeSeek_DoesNotCountAsProgress()
    {
        // SeekToDefaultPosition() on behind-live-window recovery moves the position far ahead in one go.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 1000), 0);

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 61_000), 500));
        // ...and real playback from the new position is recognised on the following ticks.
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 61_500), 1000));
    }

    [Fact]
    public void FirstLivePrepare_PositionJumpFromZero_DoesNotCountAsProgress()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 0), 0);

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 30_000), 100));
    }

    [Fact]
    public void SlowCreep_FarBelowRealTime_DoesNotCountAsProgress()
    {
        // A few ms of drift per half second is a stalled player, not playback.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 1000), 0);

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 1020), 500));
    }

    [Fact]
    public void Reset_ForgetsProgressFromPreviousStream()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Ready, pos: 1000), 0);
        tracker.Evaluate(Snap(Ready, pos: 1500), 500);

        tracker.Reset();

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 0), 600));
    }

    [Fact]
    public void Reset_OnDiscontinuity_StopsAStaleAdvanceMaskingAStall()
    {
        // Playing, then a seek: Reset() (called on every position discontinuity) must not leave the
        // previous advance masking up to a grace period of a genuine stall.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Ready, pos: 1000), 0);
        tracker.Evaluate(Snap(Ready, pos: 1500), 500);
        tracker.Reset();

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 8000), 600));
    }

    // ---- non-playing states ----------------------------------------------------------------------

    [Theory]
    [InlineData(Idle, PlaybackStatus.Idle)]
    [InlineData(Ended, PlaybackStatus.Ended)]
    public void IdleAndEnded_NeverShowBuffering_EvenIfPositionAdvanced(int state, PlaybackStatus expected)
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 0), 0);
        tracker.Evaluate(Snap(Buffering, pos: 400), 400);

        Assert.Equal(expected, tracker.Evaluate(Snap(state, pos: 800), 800));
    }

    [Fact]
    public void Error_ReportsError_NotBuffering()
    {
        // Regression: OnPlayerErrorChanged used to set the status text to "Buffering" and leave it there.
        var tracker = new PlaybackStatusTracker();

        Assert.Equal(PlaybackStatus.Error, tracker.Evaluate(Snap(Idle, error: true), 0));
        Assert.Equal(PlaybackStatus.Error, tracker.Evaluate(Snap(Buffering, error: true), 100));
    }

    [Fact]
    public void Error_ClearsOnceNewPrepareReportsNoError()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Idle, error: true), 0);

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering), 100));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready), 200));
    }

    [Fact]
    public void UnknownState_DoesNotShowSpinner()
    {
        var tracker = new PlaybackStatusTracker();

        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(99), 0));
    }

    // ---- verdict + spinner together (the owner's "no TV needed" requirement) --------------------

    [Fact]
    public void Assess_HealthyHlsStream_NeverShowsBufferingOrSpinner()
    {
        // Realistic healthy HLS: READY from the start, isLoading flapping (not observable here, which is
        // the point), position advancing in real time, buffer at 100%.
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(Snap(Buffering, pos: 0), 0, isPreparing: true, chromeSuppressed: false);
        var ready = tracker.Assess(Snap(Ready, pos: 0, buffered: 100), 200, isPreparing: false, chromeSuppressed: false);
        Assert.Equal(PlaybackStatus.Playing, ready.Status);
        Assert.False(ready.ShowSpinner);

        for (var tick = 1; tick <= 20; tick++)
        {
            var view = tracker.Assess(
                Snap(Ready, pos: tick * 500, buffered: 100), 200 + tick * 500, isPreparing: false, chromeSuppressed: false);

            Assert.Equal(PlaybackStatus.Playing, view.Status);
            Assert.False(view.ShowSpinner);
        }
    }

    [Fact]
    public void Assess_StaleBufferingStateWhilePositionAdvances_SaysPlayingAndShowsNoSpinner()
    {
        // The review's P1: STATE_BUFFERING stuck, position moving. Text AND spinner must both follow
        // the verdict (previously the spinner stayed up for any non-READY state).
        var tracker = new PlaybackStatusTracker();
        var first = tracker.Assess(Snap(Buffering, pos: 1000), 0, isPreparing: false, chromeSuppressed: false);
        Assert.Equal(PlaybackStatus.Buffering, first.Status);
        Assert.True(first.ShowSpinner);

        var second = tracker.Assess(Snap(Buffering, pos: 1500), 500, isPreparing: false, chromeSuppressed: false);

        Assert.Equal(PlaybackStatus.Playing, second.Status);
        Assert.False(second.ShowSpinner);
        Assert.Equal(BufferingSignal.Recovered, second.Signal);
    }

    [Fact]
    public void Assess_GenuineStall_ShowsBufferingAndSpinnerUntilPositionMoves()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(Snap(Ready, pos: 1000), 0, false, false);
        tracker.Assess(Snap(Ready, pos: 1500), 500, false, false);

        var stalled = tracker.Assess(Snap(Buffering, pos: 1500), 1000, false, false);
        Assert.Equal(PlaybackStatus.Playing, stalled.Status); // still inside the progress grace

        var stillStalled = tracker.Assess(Snap(Buffering, pos: 1500), 3000, false, false);
        Assert.Equal(PlaybackStatus.Buffering, stillStalled.Status);
        Assert.True(stillStalled.ShowSpinner);
        Assert.Equal(BufferingSignal.Started, stillStalled.Signal);

        var recovered = tracker.Assess(Snap(Ready, pos: 1500), 3100, false, false);
        Assert.Equal(PlaybackStatus.Playing, recovered.Status);
        Assert.False(recovered.ShowSpinner);
        Assert.Equal(BufferingSignal.Recovered, recovered.Signal);
    }

    [Fact]
    public void Assess_Error_ShowsSpinnerOnlyWhileFailoverIsPlausible()
    {
        var tracker = new PlaybackStatusTracker();
        var t0 = 10_000L;

        Assert.True(tracker.Assess(Snap(Idle, error: true), t0, false, false).ShowSpinner);
        Assert.True(tracker.Assess(Snap(Idle, error: true), t0 + PlaybackWaitChrome.FailoverGraceMs - 1, false, false).ShowSpinner);

        // Failover never attached: the spinner goes, the Error text stays.
        var held = tracker.Assess(Snap(Idle, error: true), t0 + 10_000, false, false);
        Assert.Equal(PlaybackStatus.Error, held.Status);
        Assert.False(held.ShowSpinner);
    }

    [Fact]
    public void Assess_ErrorThenAttach_ShowsSpinnerAgainWhilePreparing()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(Snap(Idle, error: true), 0, false, false);
        Assert.False(tracker.Assess(Snap(Idle, error: true), 20_000, false, false).ShowSpinner);

        var attaching = tracker.Assess(Snap(Idle, error: true), 20_100, isPreparing: true, chromeSuppressed: false);

        Assert.True(attaching.ShowSpinner);
    }

    [Fact]
    public void Assess_IdleErrorFlip_DoesNotRestartTheFailoverWindow()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(Snap(Idle, error: true), 0, false, false);
        tracker.Assess(Snap(Idle), 3000, false, false);

        Assert.False(tracker.Assess(Snap(Idle, error: true), PlaybackWaitChrome.FailoverGraceMs + 1, false, false).ShowSpinner);
    }

    [Fact]
    public void Assess_IdleAfterStopOnClose_NeverShowsSpinner()
    {
        // Back -> Stop() -> IDLE callback: the closing flag must win over the failover rule.
        var tracker = new PlaybackStatusTracker();
        tracker.Assess(Snap(Ready, pos: 1000), 0, false, false);

        var view = tracker.Assess(Snap(Idle, pos: 1000), 100, isPreparing: false, chromeSuppressed: true);

        Assert.Equal(PlaybackStatus.Idle, view.Status);
        Assert.False(view.ShowSpinner);
    }

    [Theory]
    [InlineData(Idle, false)]
    [InlineData(Idle, true)]
    [InlineData(Buffering, false)]
    [InlineData(Ready, true)]
    [InlineData(Ended, false)]
    public void Assess_WhileClosing_NeverShowsSpinnerInAnyState(int state, bool error)
    {
        var tracker = new PlaybackStatusTracker();

        var view = tracker.Assess(Snap(state, error: error), 0, isPreparing: true, chromeSuppressed: true);

        Assert.False(view.ShowSpinner);
    }

    // ---- buffering signal ------------------------------------------------------------------------

    [Theory]
    [InlineData(null, PlaybackStatus.Buffering, BufferingSignal.Started)]
    [InlineData(PlaybackStatus.Playing, PlaybackStatus.Buffering, BufferingSignal.Started)]
    [InlineData(PlaybackStatus.Buffering, PlaybackStatus.Buffering, BufferingSignal.None)]
    [InlineData(PlaybackStatus.Buffering, PlaybackStatus.Playing, BufferingSignal.Recovered)]
    // Leaving Buffering for these is NOT "recovered": they carry their own engine events and the session
    // controller treats Buffering(false) as recovery, so it must not be raised here.
    [InlineData(PlaybackStatus.Buffering, PlaybackStatus.Error, BufferingSignal.None)]
    [InlineData(PlaybackStatus.Buffering, PlaybackStatus.Idle, BufferingSignal.None)]
    [InlineData(PlaybackStatus.Buffering, PlaybackStatus.Ended, BufferingSignal.None)]
    [InlineData(PlaybackStatus.Playing, PlaybackStatus.Playing, BufferingSignal.None)]
    [InlineData(null, PlaybackStatus.Playing, BufferingSignal.None)]
    public void SignalFor_RaisesOnlyStallStartAndRecovery(
        PlaybackStatus? previous, PlaybackStatus current, BufferingSignal expected)
    {
        Assert.Equal(expected, PlaybackStatusTracker.SignalFor(previous, current));
    }
}
