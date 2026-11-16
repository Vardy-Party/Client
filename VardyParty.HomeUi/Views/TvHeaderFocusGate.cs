namespace VardyParty.HomeUi.Views;

/// <summary>What the TV adapter does when the auth overlay hides.</summary>
public enum TvAuthOverlayHiddenAction
{
    /// <summary>Nothing (phone, or a stream-exit focus pass owns the hold).</summary>
    None,

    /// <summary>Cards exist: re-arm first-card focus, hold Menu, start the retry.</summary>
    RearmNow,

    /// <summary>
    /// No cards yet (catalog still loading): keep Menu held and re-arm when the
    /// next non-empty board is applied. Keeps the #113 cold-start protection.
    /// </summary>
    HoldUntilCards,

    /// <summary>The board has settled empty: nothing can arm, so release Menu.</summary>
    Release,
}

/// <summary>What the TV adapter does when the view model applies a board.</summary>
public enum TvGamesUpdatedAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>A re-arm was recorded and cards have landed: re-arm and start the retry.</summary>
    RearmAndRetry,

    /// <summary>The board settled empty: no first card will arm, so release Menu.</summary>
    Release,
}

/// <summary>
/// Policy for the Android TV Menu header focus gate. The platform adapter
/// holds Menu out of the focus order until the first card is selected, so
/// cold start cannot land on Menu with the first rail off-screen (field
/// report, #113). The hold must never stay set unless a release path — the
/// first-card initial-focus retry, the stream-exit retry, or an empty board
/// settling — will run. Otherwise Up from the first row cannot reach Menu: a
/// second HomeView on the singleton <c>HomeViewModel</c> re-held Menu after
/// <c>RequestsInitialFocus</c> had already been consumed (3.6.1-b262 on the
/// Bravia, 16 Nov 2026). A stream-exit focus pass always wins over first-card
/// focus, so the watched game keeps focus.
/// </summary>
public static class TvHeaderFocusGate
{
    /// <summary>
    /// Up-time safety net only: once the hold has lasted this long, Up from the
    /// first row may reach Menu (<see cref="ShouldAllowUpToMenu"/>). Nothing
    /// releases the hold on a timer, so a slow 32-bit cold start whose catalog
    /// is still loading keeps Menu out of the default focus search.
    /// </summary>
    public static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When the Menu button is wired: hold only on television, and only if
    /// a release path will run (first-card focus is still pending, the
    /// board is still empty so the cold-start retry will arm, a stream-exit
    /// retry is in flight, or the caller will re-arm because the singleton
    /// already consumed the one-shot flag).
    /// </summary>
    public static bool ShouldHoldHeaderOnMenuWire(
        bool isTelevision,
        bool boardHasCards,
        bool initialFocusPending,
        bool streamExitPending)
    {
        if (!isTelevision)
        {
            return false;
        }

        if (streamExitPending)
        {
            return true;
        }

        if (!boardHasCards || initialFocusPending)
        {
            return true;
        }

        return ShouldRearmInitialFocusOnMenuWire(isTelevision, boardHasCards, initialFocusPending, streamExitPending);
    }

    /// <summary>
    /// A new HomeView on a singleton view model that already has games must
    /// re-arm first-card focus so the hold has a release path, unless a
    /// stream-exit focus pass is running: that pass releases the hold, and a
    /// first-card retry would race it for focus.
    /// </summary>
    public static bool ShouldRearmInitialFocusOnMenuWire(
        bool isTelevision,
        bool boardHasCards,
        bool initialFocusPending,
        bool streamExitPending) =>
        isTelevision && boardHasCards && !initialFocusPending && !streamExitPending;

    /// <summary>
    /// After the auth overlay hides (device-code sign-in completed, or the
    /// host already had tokens). On an authenticated cold start this runs
    /// straight after the games feed starts, before any card exists, so an
    /// empty board that is still loading must keep the hold.
    /// </summary>
    public static TvAuthOverlayHiddenAction DecideWhenAuthOverlayHides(
        bool isTelevision,
        bool boardHasCards,
        bool boardSettledEmpty,
        bool streamExitPending)
    {
        if (!isTelevision || streamExitPending)
        {
            return TvAuthOverlayHiddenAction.None;
        }

        if (boardHasCards)
        {
            return TvAuthOverlayHiddenAction.RearmNow;
        }

        return boardSettledEmpty
            ? TvAuthOverlayHiddenAction.Release
            : TvAuthOverlayHiddenAction.HoldUntilCards;
    }

    /// <summary>
    /// When the view model applies a board: release if it settled empty;
    /// re-arm first-card focus if a re-arm was recorded (auth overlay hid
    /// before cards existed) and cards have now landed, unless a stream-exit
    /// focus pass is running.
    /// </summary>
    public static TvGamesUpdatedAction DecideOnGamesUpdated(
        bool isTelevision,
        bool boardHasCards,
        bool boardSettledEmpty,
        bool rearmOnNextCards,
        bool streamExitPending)
    {
        if (!isTelevision)
        {
            return TvGamesUpdatedAction.None;
        }

        if (boardSettledEmpty)
        {
            return TvGamesUpdatedAction.Release;
        }

        return rearmOnNextCards && boardHasCards && !streamExitPending
            ? TvGamesUpdatedAction.RearmAndRetry
            : TvGamesUpdatedAction.None;
    }

    /// <summary>
    /// Up from the first row may reach Menu when the hold is off, when any
    /// card has already had focus, or when the hold's bounded timeout has
    /// elapsed. This is the safety net if the hold is stuck.
    /// </summary>
    public static bool ShouldAllowUpToMenu(
        bool holdActive,
        bool anyCardHasHadFocus,
        bool holdTimedOut) =>
        !holdActive || anyCardHasHadFocus || holdTimedOut;

    public static bool IsHoldTimedOut(long holdStartedMs, long nowMs, long timeoutMs) =>
        nowMs - holdStartedMs >= timeoutMs;

    public static long HoldTimeoutMilliseconds => (long)HoldTimeout.TotalMilliseconds;
}
