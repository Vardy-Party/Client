namespace VardyParty.Playback;

/// <summary>
/// Immutable view of session state for tests, logging, and AI observation.
/// </summary>
public sealed class PlaybackSessionSnapshot
{
    public PlaybackSessionState State { get; init; }
    public long AttachGeneration { get; init; }
    public string? CurrentUrl { get; init; }
    public string? LastGoodUrl { get; init; }
    public bool HasEstablishedPlayback { get; init; }
    public bool IsPreparing { get; init; }
    public bool UsedCachedUrl { get; init; }
    public bool CacheRetryUsed { get; init; }
    public int HealthyStreamCount { get; init; }
    public bool IsBuffering { get; init; }
    public int ConsecutiveDownloadFailures { get; init; }
}
