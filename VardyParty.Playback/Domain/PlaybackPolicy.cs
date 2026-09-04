using VardyParty.Kernel;

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
    /// User Next/Prev never marks a stream bad — only switches when the pool has another entry.
    /// Preparing does NOT block: ExoPlayer can stick in BUFFERING without STATE_READY
    /// (field: Android TV "Switch requested…" toast with no switch). User navigation
    /// abandons the stuck prepare so the next candidate can attach.
    /// </summary>
    public static bool CanUserNavigate(PlaybackSessionSnapshot snapshot, int healthyStreamCount)
    {
        if (snapshot.State is PlaybackSessionState.Closed or PlaybackSessionState.Failed)
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
    /// Windows MediaFailed-on-start currently closes immediately; unified rule is advance-if-pool.
    /// </summary>
    public static bool ShouldAdvanceAfterFailedStart(PlaybackSessionSnapshot snapshot, int healthyStreamCountAfterRemove)
        => !snapshot.HasEstablishedPlayback
           && healthyStreamCountAfterRemove >= 1;

    /// <summary>
    /// Windows AdaptiveMediaSource counted consecutive segment/manifest HTTP failures.
    /// Unified: N consecutive download failures are a hard fail (same as Error).
    /// </summary>
    public const int MaxConsecutiveDownloadFailures = 5;

    public static bool IsHardDownloadFailure(int consecutiveFailures)
        => consecutiveFailures >= MaxConsecutiveDownloadFailures;

    /// <summary>
    /// Soft live-HLS recoveries (Android BehindLiveWindow seek, Windows AdaptiveMediaSource reattach)
    /// before escalating to <see cref="MediaEngineEventKind.Error"/> / pool remove.
    /// Linux LibVLC uses --http-reconnect instead of this budget.
    /// </summary>
    public const int MaxLiveHlsRecoveries = 5;

    /// <summary>
    /// WinUI <c>AdaptiveMediaSource.DesiredLiveOffset</c> backoff from the live edge (seconds).
    /// Larger = more tolerant of brief rebuffers (pairs with Android LoadControl buffers).
    /// </summary>
    public const int DesiredLiveOffsetSeconds = 25;

    /// <summary>
    /// Media3 <c>PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW</c> (1002).
    /// Hosts pass <c>error.ErrorCode</c>; keep the numeric here so Core tests stay OS-free.
    /// </summary>
    public const int ExoPlayerErrorCodeBehindLiveWindow = 1002;

    /// <summary>
    /// Whether the host may soft-recover (seek/reattach) instead of raising Error.
    /// </summary>
    public static bool ShouldAttemptLiveHlsRecovery(int recoveriesAlreadyAttempted, string? currentPlaybackUrl)
        => recoveriesAlreadyAttempted < MaxLiveHlsRecoveries
           && !string.IsNullOrWhiteSpace(currentPlaybackUrl);

    /// <summary>
    /// Android ExoPlayer fell behind the HLS live window — recoverable by seek-to-live-edge.
    /// </summary>
    public static bool IsBehindLiveWindowFailure(int? errorCode, string? message, string? causeSummary = null)
    {
        if (errorCode == ExoPlayerErrorCodeBehindLiveWindow)
            return true;

        if (ContainsLiveWindowMarker(message))
            return true;

        return ContainsLiveWindowMarker(causeSummary);
    }

    /// <summary>
    /// Windows MediaPlayer failure classification for live HLS soft-recover.
    /// Permanent categories (unsupported / aborted / auth) must escalate.
    /// </summary>
    public static bool IsRecoverableLiveHlsMediaFailure(
        bool isNetworkError,
        bool isDecodingError,
        bool isUnknownError,
        bool isSourceNotSupported,
        bool isAborted,
        string? detailMessage = null)
    {
        if (isSourceNotSupported || isAborted)
            return false;

        if (IsPermanentLiveHlsAuthOrUnsupported(detailMessage))
            return false;

        return isNetworkError || isDecodingError || isUnknownError;
    }

    /// <summary>
    /// Shared reject list: auth / explicit unsupported signals in OS error text.
    /// </summary>
    public static bool IsPermanentLiveHlsAuthOrUnsupported(string? detailMessage)
    {
        if (string.IsNullOrWhiteSpace(detailMessage))
            return false;

        var detail = detailMessage.ToLowerInvariant();
        return detail.Contains("403")
               || detail.Contains("401")
               || detail.Contains("not supported", StringComparison.Ordinal);
    }

    private static bool ContainsLiveWindowMarker(string? text)
        => !string.IsNullOrEmpty(text)
           && text.Contains("BehindLiveWindow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ExoPlayer raises OnPlayerErrorChanged(null) to clear a previous error. Hosts must not
    /// translate that into <see cref="MediaEngineEventKind.Error"/> (legacy Android auto-switched).
    /// </summary>
    public static bool ShouldIgnoreClearedEngineError(bool errorIsNull) => errorIsNull;

    /// <summary>
    /// Cached M3U8 retry: only attach the fresh URL if it exists and differs (token/CDN rotation).
    /// </summary>
    public static bool ShouldAcceptFreshM3U8(string? failedCachedUrl, string? freshUrl)
        => StreamCandidateRules.ShouldAcceptFreshM3U8(failedCachedUrl, freshUrl);

    /// <summary>Countdown pages are not playable candidates.</summary>
    public static bool ShouldSkipCountdown(bool isCountdown)
        => StreamCandidateRules.ShouldSkipCountdown(isCountdown);
}
