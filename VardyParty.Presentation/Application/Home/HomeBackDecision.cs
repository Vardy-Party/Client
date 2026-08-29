namespace VardyParty.Presentation;

/// <summary>
/// The single hardware-Back decision for the TV homepage. Field evidence
/// (32-bit TV, saturated main thread): Back with the menu open CLOSED the
/// menu and cleared suppression synchronously, but the panel kept showing the
/// open menu for over a second — the user pressed Back again against the
/// stale frame and the "unhandled Back exits the app" rule fired. The grace
/// window absorbs those stale presses: once an overlay consumes Back, an
/// app-exit Back is ignored until the window passes (a deliberate exit just
/// needs one more press afterwards — far better than an accidental exit).
/// Pure and clock-injected so the decision is unit-testable headless.
/// </summary>
public static class HomeBackDecision
{
    /// <summary>
    /// How long after an overlay-consumed Back an app-exit Back is ignored.
    /// Sized to cover the observed worst-case frame gap (~1.3s full-tree
    /// pass) so a repeat press against a stale frame can never exit.
    /// </summary>
    public static readonly TimeSpan ExitGrace = TimeSpan.FromMilliseconds(1500);

    public enum BackAction
    {
        /// <summary>
        /// An overlay (menu / device-code sign-in / stream resolution) is
        /// visible: Back belongs to the overlay chain, never navigation.
        /// </summary>
        DelegateToOverlays,

        /// <summary>
        /// No overlay is visible but one consumed Back moments ago — this
        /// press is likely aimed at a frame that no longer reflects state.
        /// Do nothing.
        /// </summary>
        IgnoreStaleExit,

        /// <summary>Nothing is open and no recent overlay Back: exit the app.</summary>
        ExitApp,
    }

    /// <param name="overlaySuppressed">Any overlay currently registered visible.</param>
    /// <param name="lastOverlayBackMs">Timestamp (monotonic ms) of the last overlay-consumed Back; 0 = never.</param>
    /// <param name="nowMs">Current monotonic ms (Environment.TickCount64).</param>
    public static BackAction Decide(bool overlaySuppressed, long lastOverlayBackMs, long nowMs)
    {
        if (overlaySuppressed)
        {
            return BackAction.DelegateToOverlays;
        }

        if (lastOverlayBackMs != 0 && nowMs - lastOverlayBackMs < (long)ExitGrace.TotalMilliseconds)
        {
            return BackAction.IgnoreStaleExit;
        }

        return BackAction.ExitApp;
    }
}
