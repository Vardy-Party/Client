namespace VardyParty.Playback;

/// <summary>Where the native player activity is in its life, as far as wait chrome and resume are concerned.</summary>
public enum PlaybackLifecycle
{
    /// <summary>In the foreground with a live player: status events and the watchdog drive the spinner.</summary>
    Active,

    /// <summary>
    /// Paused without finishing (Home, another app, a TV system overlay). The player was stopped on purpose,
    /// so its IDLE is not a failure and must not raise a spinner. It is expected to come back.
    /// </summary>
    Suspended,

    /// <summary>The viewer left, or the activity is finishing or destroyed. Terminal.</summary>
    Closing
}

/// <summary>What to do with the player when the activity returns to the foreground.</summary>
public enum ResumeAction
{
    None,

    /// <summary>Re-attach the current stream: re-adds the listener, restarts the watchdog, shows the spinner only while preparing.</summary>
    Reattach
}

/// <summary>
/// The pause / resume / close rules of the native player, kept pure so they are unit-tested.
/// The platform layer only maps Activity callbacks onto these functions.
/// </summary>
public static class PlaybackLifecycleRules
{
    /// <summary>
    /// A pause either suspends the player or, when the activity is finishing, closes it. Closing is terminal:
    /// nothing brings a closing player back to Suspended or Active.
    /// </summary>
    public static PlaybackLifecycle OnPause(PlaybackLifecycle current, bool isFinishing)
        => current == PlaybackLifecycle.Closing || isFinishing
            ? PlaybackLifecycle.Closing
            : PlaybackLifecycle.Suspended;

    /// <summary>Resuming wakes a suspended player. A closing one stays closing.</summary>
    public static PlaybackLifecycle OnResume(PlaybackLifecycle current)
        => current == PlaybackLifecycle.Suspended ? PlaybackLifecycle.Active : current;

    /// <summary>
    /// Only an active player owns wait chrome. A suspended player has been stopped deliberately and a
    /// closing one is going away, so neither may show a spinner, whatever state Stop() left the engine in.
    /// </summary>
    public static bool SuppressesWaitChrome(PlaybackLifecycle lifecycle) => lifecycle != PlaybackLifecycle.Active;

    /// <summary>
    /// A suspended player was stopped, so coming back must re-attach it (otherwise the viewer is left with a
    /// stopped player). Nothing to do without a player or a stream, or when not coming back from a suspend.
    /// </summary>
    public static ResumeAction OnResumeAction(PlaybackLifecycle before, bool hasPlayer, bool hasStream)
        => before == PlaybackLifecycle.Suspended && hasPlayer && hasStream
            ? ResumeAction.Reattach
            : ResumeAction.None;
}
