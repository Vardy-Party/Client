namespace VardyParty.Presentation;

/// <summary>
/// Finding-streams modal lifecycle rules shared by Android TV / phone and Linux.
/// </summary>
public static class HomeFindingStreamsPolicy
{
    /// <summary>
    /// Hardware Back while the finding-streams modal is owned must cancel
    /// discovery (same as tapping Cancel) — never exit the app.
    /// </summary>
    public static bool HardwareBackShouldCancelFinding(bool findingModalOwned) =>
        findingModalOwned;

    /// <summary>
    /// Leaving playback (Back / close stream) always cancels discovery so headed
    /// Chrome <c>/mp</c> stops with the player, even if the finding overlay
    /// already dismissed and sibling chips are still in flight.
    /// </summary>
    public static bool PlaybackLeaveShouldStopFinding(bool findingActive)
    {
        _ = findingActive;
        return true;
    }

    /// <summary>
    /// True when any host flag says the finding-streams session is still owned
    /// (modal open, resolve claimed, or task in flight).
    /// </summary>
    public static bool IsFindingActive(
        bool resolveOverlayOpen,
        bool isResolvingStreams,
        bool resolutionStartClaimed,
        bool resolutionTaskInFlight) =>
        resolveOverlayOpen
        || isResolvingStreams
        || resolutionStartClaimed
        || resolutionTaskInFlight;
}
