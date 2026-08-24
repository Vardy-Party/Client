namespace VardyParty.Playback;

/// <summary>
/// Collapses a batch of <see cref="PlaybackEffect"/>s into host actions.
/// RemoveCurrent already lands the pool index on the next remaining stream, so hosts
/// must attach current after remove — not call SwitchToNext (that would skip).
/// </summary>
public sealed record PlaybackCommand(
    string? AttachUrl = null,
    bool AttachIsRevert = false,
    bool ClearResolvedUrl = false,
    bool RemoveCurrentFromPool = false,
    bool SwitchPoolToNext = false,
    bool AttachCurrentAfterRemove = false,
    bool SwitchPoolToPrevious = false,
    bool RetryFreshResolve = false,
    bool Stop = false,
    bool CloseSession = false,
    string? CloseReason = null,
    bool RaiseBuffering = false,
    bool IsBuffering = false,
    bool ReportFailed = false,
    bool ReportDeclined = false,
    bool ReportWorking = false,
    bool MarkEstablished = false,
    string? Reason = null)
{
    public bool IsNoOp =>
        AttachUrl is null
        && !ClearResolvedUrl
        && !RemoveCurrentFromPool
        && !SwitchPoolToNext
        && !AttachCurrentAfterRemove
        && !SwitchPoolToPrevious
        && !RetryFreshResolve
        && !Stop
        && !CloseSession
        && !RaiseBuffering
        && !ReportFailed
        && !ReportDeclined
        && !ReportWorking
        && !MarkEstablished;

    public static PlaybackCommand FromEffects(IReadOnlyList<PlaybackEffect> effects)
    {
        if (effects.Count == 0)
            return new PlaybackCommand();

        string? attachUrl = null;
        var attachIsRevert = false;
        var clear = false;
        var remove = false;
        var advance = false;
        var previous = false;
        var retry = false;
        var stop = false;
        var close = false;
        string? closeReason = null;
        var raiseBuf = false;
        var isBuf = false;
        var reportFailed = false;
        var reportDeclined = false;
        var reportWorking = false;
        var mark = false;
        string? reason = null;

        foreach (var e in effects)
        {
            if (!string.IsNullOrWhiteSpace(e.Reason))
                reason = e.Reason;

            switch (e.Kind)
            {
                case PlaybackEffectKind.Attach:
                    attachUrl = e.Url;
                    attachIsRevert = false;
                    break;
                case PlaybackEffectKind.RevertToLastGood:
                    attachUrl = e.Url;
                    attachIsRevert = true;
                    break;
                case PlaybackEffectKind.ClearResolvedUrl:
                    clear = true;
                    break;
                case PlaybackEffectKind.RemoveCurrentFromPool:
                    remove = true;
                    break;
                case PlaybackEffectKind.AdvanceToNext:
                    advance = true;
                    break;
                case PlaybackEffectKind.AdvanceToPrevious:
                    previous = true;
                    break;
                case PlaybackEffectKind.RetryFreshResolve:
                    retry = true;
                    break;
                case PlaybackEffectKind.Stop:
                    stop = true;
                    break;
                case PlaybackEffectKind.CloseSession:
                    close = true;
                    closeReason = e.Reason;
                    break;
                case PlaybackEffectKind.RaiseBuffering:
                    raiseBuf = true;
                    isBuf = string.Equals(e.Reason, "buffering", StringComparison.OrdinalIgnoreCase);
                    break;
                case PlaybackEffectKind.ReportFailed:
                    reportFailed = true;
                    break;
                case PlaybackEffectKind.ReportDeclined:
                    reportDeclined = true;
                    break;
                case PlaybackEffectKind.ReportWorking:
                    reportWorking = true;
                    break;
                case PlaybackEffectKind.MarkEstablished:
                    mark = true;
                    break;
            }
        }

        return new PlaybackCommand(
            AttachUrl: attachUrl,
            AttachIsRevert: attachIsRevert,
            ClearResolvedUrl: clear,
            RemoveCurrentFromPool: remove,
            SwitchPoolToNext: advance && !remove && !attachIsRevert,
            AttachCurrentAfterRemove: advance && remove && !attachIsRevert,
            SwitchPoolToPrevious: previous,
            RetryFreshResolve: retry,
            Stop: stop,
            CloseSession: close,
            CloseReason: closeReason,
            RaiseBuffering: raiseBuf,
            IsBuffering: isBuf,
            ReportFailed: reportFailed,
            ReportDeclined: reportDeclined,
            ReportWorking: reportWorking,
            MarkEstablished: mark,
            Reason: reason);
    }
}
