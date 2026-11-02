namespace VardyParty.Playback;

/// <summary>
/// Interprets a <see cref="PlaybackCommand"/> with the Android/Windows flag order.
/// Switch-pool runs after index-switch suppression is released. CloseSession skips switch.
/// </summary>
public static class PlaybackCommandExecutor
{
    /// <returns>
    /// True when the host closed the session and the caller should return without further work.
    /// </returns>
    public static bool Apply(PlaybackCommand cmd, IPlaybackCommandHost host)
    {
        if (cmd.IsNoOp)
            return false;

        host.BeginIndexSwitchSuppression();
        try
        {
            // Report BEFORE clear or remove, while the pool's current row is still
            // the stream that failed. Hosts key the report on that row's catalog URL.
            if (cmd.ReportFailed)
                host.ReportFailed(cmd.Reason);

            if (cmd.ReportDeclined)
                host.ReportDeclined(cmd.Reason ?? "Health declined");

            if (cmd.ClearResolvedUrl)
                host.ClearCurrentResolvedUrl();

            if (cmd.RemoveCurrentFromPool)
                host.RemoveCurrentFromPool();

            host.SyncHealthyStreamCount();

            if (cmd.MarkEstablished)
                host.MarkEstablished();

            if (cmd.ReportWorking)
                host.ReportWorking();

            if (cmd.RaiseBuffering)
                host.RaiseBuffering(cmd.IsBuffering);

            if (!string.IsNullOrWhiteSpace(cmd.AttachUrl))
            {
                // A failed switch leaves the pool index on the target row. Point it
                // back at the last-good row (still suppressed) before re-attaching it.
                if (cmd.AttachIsRevert)
                    host.RevertPoolIndexTo(cmd.AttachUrl);
                host.Attach(cmd.AttachUrl, cmd.AttachIsRevert);
            }
            else if (cmd.AttachCurrentAfterRemove)
                host.AttachCurrentAfterRemove();

            if (cmd.RetryFreshResolve)
                host.RetryFreshResolve();

            if (cmd.Stop)
                host.StopEngine();

            if (cmd.CloseSession)
            {
                host.CloseSession(cmd.CloseReason ?? cmd.Reason ?? "Playback failed");
                return true;
            }
        }
        catch (Exception ex)
        {
            host.NotifyApplyFailed(ex);
        }
        finally
        {
            host.EndIndexSwitchSuppression();
        }

        if (cmd.SwitchPoolToNext)
            host.SwitchPoolToNext();
        else if (cmd.SwitchPoolToPrevious)
            host.SwitchPoolToPrevious();

        return false;
    }
}
