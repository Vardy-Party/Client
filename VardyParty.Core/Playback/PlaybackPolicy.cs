using VardyParty.Services;

namespace VardyParty.Playback;

/// <summary>
/// Pure business rules for unified stream playback.
/// Platforms must not reimplement these branches — call <see cref="PlaybackSessionController"/> instead.
/// </summary>
public static class PlaybackPolicy
{
    /// <summary>
    /// Whether the engine may attach a new URL (same rules as legacy SwitchingDecision).
    /// </summary>
    public static bool CanAttach(string? currentUrl, string? candidateUrl, bool isPreparing)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl)) return false;
        if (isPreparing) return false;
        if (!string.IsNullOrEmpty(currentUrl) &&
            string.Equals(currentUrl, candidateUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// User Next/Prev never marks a stream bad — only switches when attach is allowed and pool has another entry.
    /// </summary>
    public static bool CanUserNavigate(PlaybackSessionSnapshot snapshot, int healthyStreamCount)
    {
        if (snapshot.State is PlaybackSessionState.Closed or PlaybackSessionState.Failed)
            return false;
        if (snapshot.IsPreparing)
            return false;
        return healthyStreamCount > 1;
    }

    /// <summary>
    /// Stale engine errors/ready from a previous attach generation must be ignored.
    /// </summary>
    public static bool IsCurrentGeneration(PlaybackSessionSnapshot snapshot, long eventGeneration)
        => eventGeneration == snapshot.AttachGeneration;

    /// <summary>
    /// Soft health decline using the shared metrics window thresholds.
    /// </summary>
    public static bool IsHealthDeclined(StreamMetricsWindow window)
        => window.IsHealthDeclined();

    /// <summary>
    /// Cached M3U8 may be retried once with a fresh resolve before treating start as failed.
    /// </summary>
    public static bool ShouldRetryFreshResolve(PlaybackSessionSnapshot snapshot)
        => snapshot.UsedCachedUrl
           && !snapshot.CacheRetryUsed
           && !snapshot.HasEstablishedPlayback;

    /// <summary>
    /// After a failed switch (had last-good, never established the new generation), revert instead of advancing.
    /// </summary>
    public static bool ShouldRevertAfterFailedSwitch(PlaybackSessionSnapshot snapshot)
        => snapshot.HasEstablishedPlayback
           && snapshot.State == PlaybackSessionState.Switching
           && !string.IsNullOrWhiteSpace(snapshot.LastGoodUrl);

    /// <summary>
    /// Hard failure while already playing (or buffering): remove and advance when another healthy exists.
    /// </summary>
    public static bool ShouldAdvanceAfterEstablishedFailure(PlaybackSessionSnapshot snapshot, int healthyStreamCount)
        => snapshot.HasEstablishedPlayback
           && snapshot.State is PlaybackSessionState.Playing or PlaybackSessionState.Buffering
           && healthyStreamCount > 1;

    /// <summary>
    /// Start never established: advance if pool has another candidate after removal, else close.
    /// </summary>
    public static bool ShouldAdvanceAfterFailedStart(PlaybackSessionSnapshot snapshot, int healthyStreamCountAfterRemove)
        => !snapshot.HasEstablishedPlayback
           && healthyStreamCountAfterRemove >= 1;
}
