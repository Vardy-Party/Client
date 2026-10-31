namespace VardyParty.Playback;

/// <summary>
/// Audio-only detection and the "tried this cycle" loop guard
/// (docs/STREAM_PLAYBACK_RULES.md "Audio-only streams" and "No loops").
/// </summary>
public static partial class PlaybackPolicy
{
    /// <summary>
    /// How long a stream may play (after Ready) without a picture before it is failed
    /// as audio-only. premium261 played looping AAC at 0x0 on 31 Oct 2026 and was
    /// reported working.
    /// </summary>
    public static readonly TimeSpan NoVideoTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Failure reason for a stream that plays sound but no picture.</summary>
    public const string NoVideoReason = "No video (audio only)";

    /// <summary>
    /// How long a stream (pool row, and its feed when the failure is about the media)
    /// that failed stays skipped by Next, auto-advance and revert. After this the
    /// background retry may try it again.
    /// </summary>
    public static readonly TimeSpan FailedInCycleTtl = TimeSpan.FromMinutes(2);

    /// <summary>Shown when Next has nowhere untried to go. The user stays where they are.</summary>
    public const string NoOtherWorkingStreamText = "No other working stream right now";

    /// <summary>Close reason when every stream has been tried and none works.</summary>
    public const string NoWorkingStreamReason = "No working stream right now \u2014 retrying in background";

    /// <summary>
    /// Whether a failure marks the FEED (m3u8 identity) as failed, not just the pool row.
    /// A rejected or expired token is about that one URL: another source page for the same
    /// feed gets its own fresh token, so it may still play (hidden fallbacks are promoted for
    /// exactly that). Every other stream failure (no video, dead, declined, retries used up)
    /// is about the media, so any row that resolves to that feed is the same failure.
    /// </summary>
    public static bool FailureMarksFeed(string? reason)
        => ClassifyPlaybackError(string.IsNullOrWhiteSpace(reason) ? "Playback error" : reason)
           != PlaybackErrorKind.TokenOrPlaylistInvalid;

    /// <summary>True when a stream has been Ready for at least <paramref name="timeout"/> with no picture.</summary>
    public static bool HasNoVideoTimedOut(DateTimeOffset? readyAt, DateTimeOffset now, TimeSpan timeout)
        => readyAt is { } at && now - at >= timeout;
}