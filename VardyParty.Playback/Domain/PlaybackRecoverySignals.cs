namespace VardyParty.Playback;

/// <summary>
/// Process-wide playback recovery signals for services Playback cannot reference
/// (the upcoming-stream prewarmer lives in Streaming; Hosting wires it up).
/// </summary>
public static class PlaybackRecoverySignals
{
    /// <summary>
    /// Raised when the first same-stream fresh <c>/play</c> retry of a recovery starts:
    /// the playing row is flaky, so the next rows should be warm before Next is needed.
    /// </summary>
    public static event Action? SameStreamRetryStarted;

    /// <summary>
    /// A new token for the stream that is already playing. The host swaps to it
    /// without the wait spinner. The picture stays up until the player prepares the new link.
    /// </summary>
    public static event Action<string>? PlayingTokenReady;

    public static void RaisePlayingTokenReady(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        var handlers = PlayingTokenReady;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(url);
            }
            catch
            {
                // A listener must never break the refresh.
            }
        }
    }

    internal static void RaiseSameStreamRetryStarted()
    {
        var handlers = SameStreamRetryStarted;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // A listener must never break recovery.
            }
        }
    }
}
