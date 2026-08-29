using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// What the host page must do after one stream-resolution outcome:
/// whether the shell selection (picked game) is released, and which error —
/// if any — goes on the homepage banner.
/// </summary>
/// <param name="ClearSelection">
/// Release <c>HomeShellViewModel</c>/<c>SelectionState</c> so a repeat click
/// on the same card starts a fresh resolution instead of being swallowed by
/// the "already running for this game" guard.
/// </param>
/// <param name="ErrorMessage">Banner copy, or null when nothing to surface.</param>
public sealed record StreamResolutionOutcomePlan(bool ClearSelection, string? ErrorMessage);

/// <summary>
/// Shared outcome handling for the stream-resolution host pages (Desktop's
/// DesktopHomePage and the MAUI head's HomeHostPage). Field failure this
/// codifies: the no-working-streams outcome left the picked game latched as
/// the shell selection and surfaced nothing, so the card looked dead — the
/// same-game guard ate every re-click until an app restart. EVERY terminal
/// outcome now maps to an explicit plan; failure outcomes always release the
/// selection and always say something.
/// </summary>
public static class StreamResolutionOutcomeUx
{
    /// <summary>Banner for the no-healthy-streams dead end.</summary>
    public const string NoHealthyStreamsMessage =
        "No healthy streams found — try again or pick another game";

    /// <summary>
    /// Banner when the orchestrator refused to start because the previous
    /// resolution session hadn't released yet (see
    /// <c>StreamResolutionOutcome.StartRefused</c>). Without this the click
    /// was a complete no-op — no overlay, no banner, nothing.
    /// </summary>
    public const string ResolverBusyMessage =
        "Still finishing the previous stream — try again in a moment";

    /// <summary>Fallback when playback failed without its own message.</summary>
    public const string StreamUnavailableMessage = "Stream unavailable";

    /// <summary>Plan for a normally-delivered outcome (no exception thrown).</summary>
    public static StreamResolutionOutcomePlan Plan(StreamResolutionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.UserClosed)
        {
            return new StreamResolutionOutcomePlan(ClearSelection: true, ErrorMessage: null);
        }

        if (outcome.NoWorkingStreams)
        {
            return new StreamResolutionOutcomePlan(ClearSelection: true, NoHealthyStreamsMessage);
        }

        if (outcome.StartRefused)
        {
            return new StreamResolutionOutcomePlan(ClearSelection: true, ResolverBusyMessage);
        }

        if (outcome.PlaybackResult is { Success: false } failed)
        {
            var message = string.IsNullOrWhiteSpace(failed.Message)
                ? StreamUnavailableMessage
                : failed.Message;
            return new StreamResolutionOutcomePlan(ClearSelection: true, message);
        }

        // Natural end of a successful session: the resume-after-player
        // decision (HomePlaybackIntent.DecideResumeAfterPlayer) owns the
        // selection from here.
        return new StreamResolutionOutcomePlan(ClearSelection: false, ErrorMessage: null);
    }

    /// <summary>Plan for an exception escaping the resolution flow.</summary>
    public static StreamResolutionOutcomePlan PlanException(string? exceptionMessage) =>
        new(ClearSelection: true,
            string.IsNullOrWhiteSpace(exceptionMessage) ? StreamUnavailableMessage : exceptionMessage);
}
