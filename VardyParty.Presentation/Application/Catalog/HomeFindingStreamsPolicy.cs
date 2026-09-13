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
    /// Leaving playback (Back / close stream) while discovery is still running
    /// must stop the operation and dismiss the modal.
    /// </summary>
    public static bool PlaybackLeaveShouldStopFinding(bool findingActive) =>
        findingActive;

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
