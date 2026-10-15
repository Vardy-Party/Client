namespace VardyParty.Playback;

/// <summary>User-visible playback status shown in the video-info overlay and driving the wait spinner.</summary>
public enum PlaybackStatus
{
    Idle,
    Buffering,
    Playing,
    Ended,
    Error
}

/// <summary>What the media engine should be told when the verdict changes.</summary>
public enum BufferingSignal
{
    /// <summary>Nothing to raise.</summary>
    None,

    /// <summary>Playback stalled: raise <c>Buffering(true)</c>.</summary>
    Started,

    /// <summary>Playback resumed after a stall: raise <c>Buffering(false)</c>.</summary>
    Recovered
}

/// <summary>
/// Point-in-time reading of the player. <see cref="PlaybackState"/> uses the Media3 <c>Player.STATE_*</c>
/// values (1 idle, 2 buffering, 3 ready, 4 ended) so the platform layer can pass them straight through.
/// </summary>
/// <param name="PlaybackState">Media3 <c>getPlaybackState()</c>.</param>
/// <param name="BufferedPercent">Media3 <c>getBufferedPercentage()</c>, 0..100. Informational: a full buffer with a frozen position is still a stall.</param>
/// <param name="PositionMs">Media3 <c>getCurrentPosition()</c>.</param>
/// <param name="HasError">A playback error has been reported and not yet cleared by a new prepare.</param>
public readonly record struct PlayerSnapshot(
    int PlaybackState,
    int BufferedPercent,
    long PositionMs,
    bool HasError = false);

/// <summary>Everything the platform layer applies after one assessment of the player.</summary>
/// <param name="Status">Status text to show.</param>
/// <param name="ShowSpinner">Whether the indeterminate wait spinner is visible.</param>
/// <param name="Signal">Buffering transition to raise to the media engine, if any.</param>
public readonly record struct PlaybackStatusView(PlaybackStatus Status, bool ShowSpinner, BufferingSignal Signal);

/// <summary>
/// Derives the playback status, the wait spinner and the buffering signal from the player's actual
/// state rather than from latched flags. All of it is pure, so it is unit-tested on any machine.
/// <para>
/// Media3 <c>onIsLoadingChanged</c> only says the player is fetching more media. It is true for most of
/// a healthy HLS stream (segment and playlist refreshes) while state is READY, so it is never read.
/// Only <c>STATE_BUFFERING</c> means playback is waiting for data. Note that Media3's <c>isPlaying()</c>
/// is defined as READY + play-when-ready + not suppressed, so it can never be true in BUFFERING and is
/// deliberately not part of the snapshot.
/// </para>
/// <para>
/// A stale BUFFERING snapshot (event race, missed READY) is overridden when the playback position is
/// demonstrably advancing at about real-time rate. A seek, a first live prepare or a live-window slide
/// moves the position by far more than the elapsed time and does not count as progress.
/// </para>
/// Feed it a snapshot on every player event and from a periodic watchdog tick.
/// </summary>
public sealed class PlaybackStatusTracker
{
    public const int StateIdle = 1;
    public const int StateBuffering = 2;
    public const int StateReady = 3;
    public const int StateEnded = 4;

    /// <summary>How long after the position last advanced at playback rate playback still counts as progressing.</summary>
    public const long ProgressGraceMs = 1200;

    /// <summary>Extra slack (ms) on the "not a jump" bound, covering clock and frame-timing jitter.</summary>
    private const long JumpSlackMs = 250;

    private long? _lastPositionMs;
    private long? _lastObservedAtMs;
    private long? _lastAdvanceAtMs;
    private PlaybackStatus? _status;
    private long? _failoverSinceMs;

    /// <summary>The verdict from the most recent evaluation, or null before the first.</summary>
    public PlaybackStatus? Current => _status;

    /// <summary>Forget position history (new stream attached, seek, live-window discontinuity, player restarted).</summary>
    public void Reset()
    {
        _lastPositionMs = null;
        _lastObservedAtMs = null;
        _lastAdvanceAtMs = null;
    }

    /// <summary>
    /// Pure rule for the Buffering event: raised when a stall starts, and when playback recovers from one.
    /// Leaving Buffering for Idle, Error or Ended is neither: those carry their own engine events and
    /// must not be reported as "recovered" (the session controller treats Buffering(false) as recovery).
    /// </summary>
    public static BufferingSignal SignalFor(PlaybackStatus? previous, PlaybackStatus current)
    {
        var wasBuffering = previous == PlaybackStatus.Buffering;
        var isBuffering = current == PlaybackStatus.Buffering;
        if (isBuffering && !wasBuffering) return BufferingSignal.Started;
        if (wasBuffering && current == PlaybackStatus.Playing) return BufferingSignal.Recovered;
        return BufferingSignal.None;
    }

    /// <param name="snapshot">Current player reading.</param>
    /// <param name="nowMs">Monotonic clock in milliseconds (e.g. SystemClock.ElapsedRealtime).</param>
    /// <param name="isPreparing">A new source is being attached (SetMediaSource / Prepare in flight).</param>
    /// <param name="isClosing">The player is being closed or left; no wait chrome may appear.</param>
    public PlaybackStatusView Assess(PlayerSnapshot snapshot, long nowMs, bool isPreparing, bool isClosing)
    {
        var previous = _status;
        var status = Evaluate(snapshot, nowMs);
        var failoverAgeMs = _failoverSinceMs is { } since ? nowMs - since : 0;
        var show = PlaybackWaitChrome.ShouldShow(status, isPreparing, isClosing, failoverAgeMs);
        return new PlaybackStatusView(status, show, SignalFor(previous, status));
    }

    /// <param name="snapshot">Current player reading.</param>
    /// <param name="nowMs">Monotonic clock in milliseconds (e.g. SystemClock.ElapsedRealtime).</param>
    public PlaybackStatus Evaluate(PlayerSnapshot snapshot, long nowMs)
    {
        ObservePosition(snapshot.PositionMs, nowMs);
        var status = Derive(snapshot, nowMs);
        Track(status, nowMs);
        return status;
    }

    private PlaybackStatus Derive(PlayerSnapshot snapshot, long nowMs)
    {
        if (snapshot.HasError) return PlaybackStatus.Error;

        switch (snapshot.PlaybackState)
        {
            case StateEnded:
                return PlaybackStatus.Ended;
            case StateIdle:
                return PlaybackStatus.Idle;
            case StateReady:
                return PlaybackStatus.Playing;
            case StateBuffering:
                return IsProgressing(nowMs) ? PlaybackStatus.Playing : PlaybackStatus.Buffering;
            default:
                // Unknown future state values: be conservative and do not show a blocking spinner.
                return PlaybackStatus.Playing;
        }
    }

    private void Track(PlaybackStatus status, long nowMs)
    {
        var inFailover = status is PlaybackStatus.Idle or PlaybackStatus.Error;
        if (!inFailover)
            _failoverSinceMs = null;
        else if (_failoverSinceMs is null)
            _failoverSinceMs = nowMs; // Idle <-> Error flips stay in the same window.

        _status = status;
    }

    private void ObservePosition(long positionMs, long nowMs)
    {
        if (_lastPositionMs is { } last && _lastObservedAtMs is { } at)
        {
            var elapsed = nowMs - at;
            var delta = positionMs - last;
            // Progress = the position moved forward at roughly the rate of the wall clock. A backwards
            // move (seek, window reset) or a jump far beyond the elapsed time (live-edge seek, first
            // live prepare, window slide) re-baselines without counting as playback.
            if (elapsed > 0 && delta > 0 && delta >= elapsed / 2 && delta <= elapsed * 2 + JumpSlackMs)
                _lastAdvanceAtMs = nowMs;
        }

        _lastPositionMs = positionMs;
        _lastObservedAtMs = nowMs;
    }

    private bool IsProgressing(long nowMs)
        => _lastAdvanceAtMs is { } at && nowMs - at <= ProgressGraceMs;
}