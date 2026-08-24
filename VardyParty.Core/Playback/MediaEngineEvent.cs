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
    UserReportBad
}
