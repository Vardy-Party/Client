namespace VardyParty.Playback;

/// <summary>
/// Facts emitted by a slim OS media engine (or by UI: next/prev/close).
/// Generation must match the attach generation that produced the event, or the session ignores it.
/// </summary>
public sealed class MediaEngineEvent
{
    public MediaEngineEventKind Kind { get; init; }

    /// <summary>Attach generation this event belongs to. Stale generations are ignored for Error/Ready.</summary>
    public long Generation { get; init; }

    public string? Message { get; init; }

    public bool? IsBuffering { get; init; }

    public int? BitrateKbps { get; init; }

    /// <summary>For UserNext/UserPrevious eligibility checks against a candidate URL.</summary>
    public string? CandidateUrl { get; init; }

    /// <summary>
    /// <see cref="MediaEngineEventKind.VideoProbe"/>: true once the engine has a video
    /// track with a picture (first frame rendered or a non-zero video size).
    /// </summary>
    public bool? HasVideo { get; init; }

    public static MediaEngineEvent Ready(long generation) => new()
    {
        Kind = MediaEngineEventKind.Ready,
        Generation = generation
    };

    public static MediaEngineEvent Buffering(long generation, bool isBuffering) => new()
    {
        Kind = MediaEngineEventKind.BufferingChanged,
        Generation = generation,
        IsBuffering = isBuffering
    };

    public static MediaEngineEvent Metrics(long generation, int? bitrateKbps = null, bool isBuffering = false) => new()
    {
        Kind = MediaEngineEventKind.MetricsSample,
        Generation = generation,
        BitrateKbps = bitrateKbps,
        IsBuffering = isBuffering
    };

    public static MediaEngineEvent Error(long generation, string? message) => new()
    {
        Kind = MediaEngineEventKind.Error,
        Generation = generation,
        Message = message
    };

    public static MediaEngineEvent Ended(long generation) => new()
    {
        Kind = MediaEngineEventKind.Ended,
        Generation = generation
    };

    /// <summary>
    /// Whether the attached stream is showing a picture. Engines send it on Ready, when a
    /// frame or a video size first arrives, and on their status tick until video is
    /// confirmed (docs/STREAM_PLAYBACK_RULES.md "Audio-only streams").
    /// </summary>
    public static MediaEngineEvent VideoProbe(long generation, bool hasVideo) => new()
    {
        Kind = MediaEngineEventKind.VideoProbe,
        Generation = generation,
        HasVideo = hasVideo
    };

    public static MediaEngineEvent UserNext() => new() { Kind = MediaEngineEventKind.UserNext };

    public static MediaEngineEvent UserPrevious() => new() { Kind = MediaEngineEventKind.UserPrevious };

    public static MediaEngineEvent UserClose() => new() { Kind = MediaEngineEventKind.UserClose };

    public static MediaEngineEvent UserReportBad(string? reason = null) => new()
    {
        Kind = MediaEngineEventKind.UserReportBad,
        Message = reason
    };
}

public enum MediaEngineEventKind
{
    Ready,
    BufferingChanged,
    MetricsSample,
    Error,
    Ended,
    UserNext,
    UserPrevious,
    UserClose,
    UserReportBad,

    /// <summary>Engine says whether the stream has video yet (see <see cref="MediaEngineEvent.VideoProbe"/>).</summary>
    VideoProbe
}
