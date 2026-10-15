namespace VardyParty.Playback;

/// <summary>
/// When the native player shows its indeterminate wait spinner. One pure decision made from the same
/// <see cref="PlaybackStatus"/> verdict that drives the status text, so the spinner and the text can
/// never disagree (a stale BUFFERING state that is really playing says "Playing" and shows no spinner).
/// </summary>
public static class PlaybackWaitChrome
{
    /// <summary>
    /// How long Idle/Error keep the spinner up on their own, waiting for failover to attach the next
    /// URL. After this the spinner goes away and the Idle/Error text stays, so the two cannot
    /// contradict each other forever when failover never attaches.
    /// </summary>
    public const long FailoverGraceMs = 5000;

    /// <param name="status">The tracker's verdict.</param>
    /// <param name="isPreparing">A new source is being attached (SetMediaSource / Prepare in flight).</param>
    /// <param name="chromeSuppressed">The player is closing or suspended (see <see cref="PlaybackLifecycleRules.SuppressesWaitChrome"/>). Never show: there is no live player for a spinner to describe.</param>
    /// <param name="failoverAgeMs">How long the player has been Idle/Error (0 when it is not).</param>
    public static bool ShouldShow(
        PlaybackStatus status,
        bool isPreparing,
        bool chromeSuppressed,
        long failoverAgeMs = 0)
    {
        if (chromeSuppressed)
            return false;

        return status switch
        {
            PlaybackStatus.Buffering => true,
            PlaybackStatus.Playing => isPreparing,
            PlaybackStatus.Idle or PlaybackStatus.Error => isPreparing || failoverAgeMs < FailoverGraceMs,
            _ => false // Ended
        };
    }
}
