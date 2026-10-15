namespace VardyParty.Playback;

/// <summary>User-visible playback status shown in the video-info overlay and driving the buffering spinner.</summary>
public enum PlaybackStatus
{
    Idle,
    Buffering,
    Playing,
    Ended,
    Error
}

/// <summary>
/// Point-in-time reading of the player. <see cref="PlaybackState"/> uses the Media3 <c>Player.STATE_*</c>
/// values (1 idle, 2 buffering, 3 ready, 4 ended) so the platform layer can pass them straight through.
/// </summary>
/// <param name="PlaybackState">Media3 <c>getPlaybackState()</c>.</param>
/// <param name="IsPlaying">Media3 <c>isPlaying()</c> (ready + play-when-ready + not suppressed).</param>
/// <param name="BufferedPercent">Media3 <c>getBufferedPercentage()</c>, 0..100.</param>
/// <param name="PositionMs">Media3 <c>getCurrentPosition()</c>.</param>
/// <param name="HasError">A playback error has been reported and not yet cleared by a new prepare.</param>
public readonly record struct PlayerSnapshot(
    int PlaybackState,
    bool IsPlaying,
    int BufferedPercent,
    long PositionMs,
    bool HasError = false);

/// <summary>
/// Derives the playback status from the player's actual state rather than from latched flags.
/// <para>
/// Media3 <c>onIsLoadingChanged</c> only says the player is fetching more media. It is true for most of
/// a healthy HLS stream (segment and playlist refreshes) while state is READY, so it must never be
/// treated as "buffering". Only <c>STATE_BUFFERING</c> means playback is waiting for data, and even
/// that is overridden when the playback position is demonstrably advancing (event races, stale state).
/// </para>
/// Feed it a snapshot on every player event and from a periodic watchdog tick; it is pure apart from
/// remembering the last position so it can detect progress.
/// </summary>
public sealed class PlaybackStatusTracker
{
    public const int StateIdle = 1;
    public const int StateBuffering = 2;
    public const int StateReady = 3;
    public const int StateEnded = 4;

    /// <summary>How long after the position last moved forward playback still counts as progressing.</summary>
    public const long ProgressGraceMs = 1500;

    private long? _lastPositionMs;
    private long? _lastAdvanceAtMs;

    /// <summary>Forget position history (new stream attached, player restarted).</summary>
    public void Reset()
    {
        _lastPositionMs = null;
        _lastAdvanceAtMs = null;
    }

    /// <param name="snapshot">Current player reading.</param>
    /// <param name="nowMs">Monotonic clock in milliseconds (e.g. SystemClock.ElapsedRealtime).</param>
    public PlaybackStatus Evaluate(PlayerSnapshot snapshot, long nowMs)
    {
        ObservePosition(snapshot.PositionMs, nowMs);

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
                if (snapshot.IsPlaying) return PlaybackStatus.Playing;
                if (IsProgressing(nowMs)) return PlaybackStatus.Playing;
                return PlaybackStatus.Buffering;
            default:
                // Unknown future state values: be conservative and do not show a blocking spinner.
                return PlaybackStatus.Playing;
        }
    }

    private void ObservePosition(long positionMs, long nowMs)
    {
        if (_lastPositionMs is { } last)
        {
            if (positionMs > last) _lastAdvanceAtMs = nowMs;
            // Backwards jump (seek, live-window reset): re-baseline without counting as progress.
        }

        _lastPositionMs = positionMs;
    }

    private bool IsProgressing(long nowMs)
        => _lastAdvanceAtMs is { } at && nowMs - at <= ProgressGraceMs;
}

