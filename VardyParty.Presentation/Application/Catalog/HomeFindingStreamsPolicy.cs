using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Whether a resolution start still queues its search, or aborts.
/// </summary>
public enum HomeResolutionStartDisposition
{
    /// <summary>This attempt is still the current generation. Queue the search.</summary>
    Continue,

    /// <summary>Generation moved and this attempt still owns the published claim.</summary>
    AbortAndClear,

    /// <summary>Generation moved and the claim is 0 or already a newer start.</summary>
    AbortAndKeep
}

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
    /// What a start does after the generation may have moved, before its
    /// search task is queued. Every mismatch aborts. Only
    /// <see cref="HomeResolutionStartDisposition.AbortAndClear"/> drops flags,
    /// and only when this attempt is still the published claim. Claim 0
    /// (viewer Back) and a newer claim leave those flags alone.
    /// </summary>
    public static HomeResolutionStartDisposition DecideResolutionStart(
        int attemptGeneration,
        int currentGeneration,
        int publishedClaimGeneration)
    {
        if (attemptGeneration == currentGeneration)
            return HomeResolutionStartDisposition.Continue;

        if (publishedClaimGeneration == attemptGeneration)
            return HomeResolutionStartDisposition.AbortAndClear;

        return HomeResolutionStartDisposition.AbortAndKeep;
    }

    /// <summary>
    /// A posted finding-streams paint belongs to the generation that queued it.
    /// Painting after that generation lost the race re-opens the overlay with
    /// nobody left to hide it.
    /// </summary>
    public static bool ShouldPaintFindingOverlay(int attemptGeneration, int currentGeneration) =>
        attemptGeneration == currentGeneration;
}
