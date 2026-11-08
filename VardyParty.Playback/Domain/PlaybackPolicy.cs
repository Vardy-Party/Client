using VardyParty.Kernel;

namespace VardyParty.Playback;

/// <summary>
/// Pure business rules for unified stream playback.
/// Platforms must not reimplement these branches — call <see cref="PlaybackSessionController"/> instead.
/// </summary>
public static partial class PlaybackPolicy
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
    /// Soft live-HLS recoveries (Android BehindLiveWindow or PlaylistStuck seek,
    /// Windows AdaptiveMediaSource reattach) before escalating to
    /// <see cref="MediaEngineEventKind.Error"/> / pool remove.
    /// Linux LibVLC uses --http-reconnect instead of this budget.
    /// </summary>
    public const int MaxLiveHlsRecoveries = 5;

    /// <summary>
    /// Live-edge backoff (seconds). WinUI <c>AdaptiveMediaSource.DesiredLiveOffset</c>;
    /// Android Media3 <c>MediaItem.LiveConfiguration.targetOffsetMs</c>. Larger = more
    /// media held behind the edge (the only place a live buffer can exist).
    /// </summary>
    public const int DesiredLiveOffsetSeconds = 25;

    /// <summary>
    /// Android Media3 <c>DefaultLoadControl</c> min buffer (ms) — how much to keep
    /// loaded around the playhead. Not a live-edge backoff; see
    /// <see cref="DesiredLiveOffsetSeconds"/>.
    /// </summary>
    public const int AndroidMinBufferMs = 30_000;

    /// <summary>Android Media3 <c>DefaultLoadControl</c> max buffer (ms).</summary>
    public const int AndroidMaxBufferMs = 60_000;

    /// <summary>Android Media3 buffer required before first play (ms).</summary>
    public const int AndroidBufferForPlaybackMs = 5_000;

    /// <summary>Android Media3 buffer required after a rebuffer (ms).</summary>
    public const int AndroidBufferForPlaybackAfterRebufferMs = 8_000;

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
    /// Android ExoPlayer <c>HlsPlaylistTracker.PlaylistStuckException</c>: the live
    /// media playlist stopped advancing. Same seek-to-live-edge recovery as
    /// <see cref="IsBehindLiveWindowFailure"/>, sharing <see cref="MaxLiveHlsRecoveries"/>.
    /// Matched from the exception message or cause summary so hosts stay OS-free.
    /// </summary>
    public static bool IsStuckLivePlaylistFailure(string? message, string? causeSummary = null)
        => ContainsStuckPlaylistMarker(message) || ContainsStuckPlaylistMarker(causeSummary);

    /// <summary>
    /// Either live-HLS stall ExoPlayer reports as a source error that a seek to the
    /// live edge can clear. Other source errors still escalate.
    /// </summary>
    public static bool IsRecoverableExoLiveHlsStall(int? errorCode, string? message, string? causeSummary = null)
    {
        // Token reject (401/403/442) must escalate to FreshResolve — soft BLW seek
        // only burns MaxLiveHlsRecoveries on a dying URL (Bravia 8 Nov 2026).
        if (IsPermanentLiveHlsAuthOrUnsupported(message)
            || IsPermanentLiveHlsAuthOrUnsupported(causeSummary))
        {
            return false;
        }

        return IsBehindLiveWindowFailure(errorCode, message, causeSummary)
               || IsStuckLivePlaylistFailure(message, causeSummary);
    }

    /// <summary>
    /// Windows MediaPlayer failure classification for live HLS soft-recover.
    /// Network-class only — mirrors Android’s BehindLiveWindow-only gate.
    /// Decoding / Unknown / unsupported / aborted / auth must escalate to Error
    /// so Ready-after-reattach cannot pin the user on a permanently dead stream.
    /// </summary>
    public static bool IsRecoverableLiveHlsMediaFailure(
        bool isNetworkError,
        bool isDecodingError,
        bool isUnknownError,
        bool isSourceNotSupported,
        bool isAborted,
        string? detailMessage = null)
    {
        // Decoding/Unknown are accepted as parameters so hosts stay typed and tests
        // can lock “not recoverable” without inventing a second API surface.
        _ = isDecodingError;
        _ = isUnknownError;

        if (!isNetworkError)
            return false;

        if (isSourceNotSupported || isAborted)
            return false;

        if (IsPermanentLiveHlsAuthOrUnsupported(detailMessage))
            return false;

        return true;
    }

    /// <summary>
    /// Shared reject list: auth / explicit unsupported signals in OS error text.
    /// </summary>
    public static bool IsPermanentLiveHlsAuthOrUnsupported(string? detailMessage)
    {
        if (string.IsNullOrWhiteSpace(detailMessage))
            return false;

        var detail = detailMessage.ToLowerInvariant();
        return ContainsHttpAuthStatus(detail)
               || detail.Contains("forbidden", StringComparison.Ordinal)
               || detail.Contains("unauthorized", StringComparison.Ordinal)
               || detail.Contains("source not supported", StringComparison.Ordinal)
               || detail.Contains("not supported", StringComparison.Ordinal)
               || detail.Contains("unsupported", StringComparison.Ordinal);
    }

    /// <summary>HTTP 401 / 403 / 442 (CDN token reject) as whole status tokens.</summary>
    private static bool ContainsHttpAuthStatus(string detailLower)
        => ContainsStatusToken(detailLower, "401")
           || ContainsStatusToken(detailLower, "403")
           || ContainsStatusToken(detailLower, "442");

    private static bool ContainsStatusToken(string text, string code)
    {
        var index = text.IndexOf(code, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsDigit(text[index - 1]);
            var afterIndex = index + code.Length;
            var after = afterIndex >= text.Length || !char.IsDigit(text[afterIndex]);
            if (before && after)
                return true;
            index = text.IndexOf(code, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Windows <c>MediaFailed</c> → session Error text. When the OS reports
    /// <c>SourceNotSupported</c>, the ErrorMessage is often a bare HRESULT with
    /// no "unsupported" words — stamp the marker so <see cref="ClassifyPlaybackError"/>
    /// returns <see cref="PlaybackErrorKind.ConfirmedDead"/> and the pool advances.
    /// Auth rejects (403 Forbidden / 0x80190193) must not get that stamp — they are
    /// <see cref="PlaybackErrorKind.TokenOrPlaylistInvalid"/>.
    /// </summary>
    public static string FormatWindowsMediaFailedMessage(
        bool isSourceNotSupported,
        string? errorMessage,
        string? extendedMessage = null)
    {
        var errMsg = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown media error" : errorMessage.Trim();
        var ext = string.IsNullOrWhiteSpace(extendedMessage) ? string.Empty : extendedMessage.Trim();
        var detail = string.IsNullOrEmpty(ext) ? errMsg : $"{errMsg} {ext}";
        // WinRT maps HTTP 403 to SourceNotSupported + Forbidden — do not label that as format death.
        if (isSourceNotSupported
            && (ContainsHttpAuthStatus(detail.ToLowerInvariant())
                || detail.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
        {
            isSourceNotSupported = false;
        }

        var head = isSourceNotSupported
            ? $"Media failed (source not supported): {errMsg}"
            : $"Media failed: {errMsg}";
        return string.IsNullOrEmpty(ext) ? head : $"{head}\n{ext}";
    }

    private static bool ContainsLiveWindowMarker(string? text)
        => !string.IsNullOrEmpty(text)
           && text.Contains("BehindLiveWindow", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsStuckPlaylistMarker(string? text)
        => !string.IsNullOrEmpty(text)
           && text.Contains("PlaylistStuck", StringComparison.OrdinalIgnoreCase);

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
