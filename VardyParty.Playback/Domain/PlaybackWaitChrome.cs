namespace VardyParty.Playback;

/// <summary>
/// When the native player must show a wait spinner. Android TV ExoPlayer
/// often stays in BUFFERING without <c>OnIsLoadingChanged(true)</c>, so a
/// spinner tied only to isLoading never appears. Keep it up on idle/error
/// too — failover attaches the next URL a beat later.
/// </summary>
public static class PlaybackWaitChrome
{
    /// <param name="isReady">ExoPlayer STATE_READY.</param>
    /// <param name="isLoading">ExoPlayer <c>OnIsLoadingChanged</c>.</param>
    /// <param name="isPreparing">Attach in flight (<c>SetMediaSource</c>/<c>Prepare</c>).</param>
    /// <param name="isEnded">ExoPlayer STATE_ENDED — not a wait, hide the spinner.</param>
    public static bool ShouldShowWaitIndicator(
        bool isReady,
        bool isLoading,
        bool isPreparing,
        bool isEnded = false)
    {
        if (isEnded)
            return false;

        if (isReady)
            return isLoading || isPreparing;

        return true;
    }
}
