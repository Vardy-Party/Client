namespace VardyParty.HomeUi.Views;

/// <summary>
/// Policy for the Android TV Menu header focus gate. The platform adapter
/// holds Menu out of the focus order until the first card is selected, so
/// cold start cannot land on Menu with the first rail off-screen (field
/// report). The hold must never stay set unless a release path — the
/// first-card initial-focus retry or the stream-exit retry — is actually
/// running. Otherwise Up from the first row cannot reach Menu: a second
/// HomeView on the singleton <c>HomeViewModel</c> re-held Menu after
/// <c>RequestsInitialFocus</c> had already been consumed (3.6.1-b262 on
/// the Bravia, 16 Nov 2026).
/// </summary>
public static class TvHeaderFocusGate
{
    /// <summary>
    /// Bounded safety timeout after which Up may reach Menu even if the
    /// hold was never released (32-bit TV cold start is generous; this is
    /// longer than the 180-frame first-card retry).
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

        return ShouldRearmInitialFocusOnMenuWire(isTelevision, boardHasCards, initialFocusPending);
    }

    /// <summary>
    /// A new HomeView on a singleton view model that already has games must
    /// re-arm first-card focus so the hold has a release path.
    /// </summary>
    public static bool ShouldRearmInitialFocusOnMenuWire(
        bool isTelevision,
        bool boardHasCards,
        bool initialFocusPending) =>
        isTelevision && boardHasCards && !initialFocusPending;

    /// <summary>
    /// After the auth overlay hides (device-code sign-in completed, or the
    /// host already had tokens), re-arm first-card focus when the board
    /// already has cards. If it does not, the adapter must release immediately.
    /// </summary>
    public static bool ShouldRearmInitialFocusWhenAuthOverlayHides(
        bool isTelevision,
        bool boardHasCards) =>
        isTelevision && boardHasCards;

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
