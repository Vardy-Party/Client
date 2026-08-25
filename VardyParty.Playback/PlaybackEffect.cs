namespace VardyParty.Playback;

/// <summary>
/// Side-effects the session controller asks the host (orchestrator / pool / engine) to perform.
/// OS code should execute Attach/Stop only; pool and resolve effects stay in Core hosts.
/// </summary>
public sealed record PlaybackEffect(
    PlaybackEffectKind Kind,
    string? Url = null,
    string? Reason = null,
    long Generation = 0);

public enum PlaybackEffectKind
{
    None,

    /// <summary>Engine should attach this M3U8 (headers supplied by host).</summary>
    Attach,

    /// <summary>Engine should stop playback.</summary>
    Stop,

    /// <summary>Current generation became playable; update last-good.</summary>
    MarkEstablished,

    /// <summary>Clear ResolvedM3U8Url on the failed pool entry (token/CDN invalid).</summary>
    ClearResolvedUrl,

    /// <summary>Remove current stream from the healthy pool.</summary>
    RemoveCurrentFromPool,

    /// <summary>Resolve (if needed) and switch pool index to next; then Attach will follow.</summary>
    AdvanceToNext,

    /// <summary>Switch pool index to previous; then Attach will follow.</summary>
    AdvanceToPrevious,

    /// <summary>Re-attach last known good M3U8 after a failed switch.</summary>
    RevertToLastGood,

    /// <summary>Same stream: resolve a fresh M3U8 and Attach once (cache-token retry).</summary>
    RetryFreshResolve,

    /// <summary>End the native player session.</summary>
    CloseSession,

    /// <summary>Crowd/health: hard failure.</summary>
    ReportFailed,

    /// <summary>Crowd/health: soft decline (buffering/bitrate/errors).</summary>
    ReportDeclined,

    /// <summary>Crowd/health: playback working / metadata.</summary>
    ReportWorking,

    /// <summary>Propagate buffering to Core observers (fix Android no-op).</summary>
    RaiseBuffering
}
