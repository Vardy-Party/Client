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
