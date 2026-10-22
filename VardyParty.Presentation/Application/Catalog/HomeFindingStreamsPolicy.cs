using VardyParty.Kernel;

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
    /// A playback hide cancels in-flight resolution only when the viewer left
    /// (Back, or the player window was destroyed). A source-error close must
    /// leave the token alive so the orchestrator can retry a fresh playlist
    /// and reopen playback. A null result keeps the historical cancel: hosts
    /// that do not report one still stop finding-streams.
    /// </summary>
    public static bool PlaybackHideShouldCancelResolution(PlaybackResult? result) =>
        result?.ViewerLeft != false;

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

    /// <summary>
    /// A pick that was allowed to start must not return while it still owns
    /// the claim. Generation mismatch before the search task is queued drops
    /// the claim, overlay, and resolving flag when this attempt is still the
    /// published owner. A newer start that has already published its own claim
    /// generation keeps the flags it wrote.
    /// </summary>
    public static bool ShouldReleaseSupersededResolutionStart(
        int attemptGeneration,
        int currentGeneration,
        int publishedClaimGeneration) =>
        attemptGeneration != currentGeneration
        && publishedClaimGeneration == attemptGeneration;

    /// <summary>
    /// A posted finding-streams paint belongs to the generation that queued it.
    /// Painting after that generation lost the race re-opens the overlay with
    /// nobody left to hide it.
    /// </summary>
    public static bool ShouldPaintFindingOverlay(int attemptGeneration, int currentGeneration) =>
        attemptGeneration == currentGeneration;
}
