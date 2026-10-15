using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackStatusTrackerTests
{
    private const int Idle = PlaybackStatusTracker.StateIdle;
    private const int Buffering = PlaybackStatusTracker.StateBuffering;
    private const int Ready = PlaybackStatusTracker.StateReady;
    private const int Ended = PlaybackStatusTracker.StateEnded;

    private static PlayerSnapshot Snap(int state, long pos = 0, bool playing = false, int buffered = 0, bool error = false)
        => new(state, playing, buffered, pos, error);

    [Fact]
    public void Ready_IsPlaying_EvenWhenPlayerIsStillLoadingMoreSegments()
    {
        // Regression: onIsLoadingChanged(true) while READY used to latch "Buffering" + spinner.
        // isLoading is not part of the snapshot any more, so a READY player can only be Playing.
        var tracker = new PlaybackStatusTracker();

        var status = tracker.Evaluate(Snap(Ready, pos: 5000, playing: true, buffered: 100), nowMs: 1000);

        Assert.Equal(PlaybackStatus.Playing, status);
    }

    [Fact]
    public void Ready_IsPlaying_EvenWhenNotYetReportedIsPlaying()
    {
        var tracker = new PlaybackStatusTracker();

        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready), 0));
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
    public void Buffering_WithPositionAdvancing_IsPlaying()
    {
        // Stale STATE_BUFFERING (event race / missed READY) while the clock is moving.
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 1000), 0);

        var status = tracker.Evaluate(Snap(Buffering, pos: 1500), 500);

        Assert.Equal(PlaybackStatus.Playing, status);
    }

    [Fact]
    public void Buffering_WithBufferFullAndIsPlaying_IsPlaying()
    {
        var tracker = new PlaybackStatusTracker();

        var status = tracker.Evaluate(Snap(Buffering, pos: 0, playing: true, buffered: 100), 0);

        Assert.Equal(PlaybackStatus.Playing, status);
    }

    [Fact]
    public void Buffering_FullBufferButFrozenPosition_StillBuffers()
    {
        // 100% buffered alone is not proof of playback: a paused/frozen player must not be reported as playing.
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
        // Events: READY -> (isLoading true) -> late STATE_BUFFERING snapshot while position keeps moving.
        var tracker = new PlaybackStatusTracker();
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 0), 0));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready, pos: 0, playing: true), 100));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready, pos: 600, playing: true), 700));
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Buffering, pos: 1200), 1300));
    }

    [Fact]
    public void StuckSpinner_Watchdog_RecoversOnNextTickWithoutAnyPlayerEvent()
    {
        // The spinner was latched on and no further events arrive; the periodic tick alone must clear it.
        var tracker = new PlaybackStatusTracker();
        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 100), 0));

        var ticks = new[]
        {
            tracker.Evaluate(Snap(Buffering, pos: 100), 500),
            tracker.Evaluate(Snap(Buffering, pos: 600), 1000),
        };

        Assert.Equal(PlaybackStatus.Buffering, ticks[0]);
        Assert.Equal(PlaybackStatus.Playing, ticks[1]);
    }

    [Fact]
    public void SeekBackwards_DoesNotCountAsProgress()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 9000), 0);

        var status = tracker.Evaluate(Snap(Buffering, pos: 2000), 500);

        Assert.Equal(PlaybackStatus.Buffering, status);
    }

    [Fact]
    public void Reset_ForgetsProgressFromPreviousStream()
    {
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Ready, pos: 1000, playing: true), 0);
        tracker.Evaluate(Snap(Ready, pos: 2000, playing: true), 500);

        tracker.Reset();

        Assert.Equal(PlaybackStatus.Buffering, tracker.Evaluate(Snap(Buffering, pos: 0), 600));
    }

    [Theory]
    [InlineData(Idle, PlaybackStatus.Idle)]
    [InlineData(Ended, PlaybackStatus.Ended)]
    public void IdleAndEnded_NeverShowBuffering_EvenIfPositionAdvanced(int state, PlaybackStatus expected)
    {
        // Regression: Idle/Ended did not hide the spinner after an earlier isLoading(true).
        var tracker = new PlaybackStatusTracker();
        tracker.Evaluate(Snap(Buffering, pos: 0), 0);
        tracker.Evaluate(Snap(Buffering, pos: 500), 400);

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
        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(Ready, playing: true), 200));
    }

    [Fact]
    public void UnknownState_DoesNotShowSpinner()
    {
        var tracker = new PlaybackStatusTracker();

        Assert.Equal(PlaybackStatus.Playing, tracker.Evaluate(Snap(99), 0));
    }
}
