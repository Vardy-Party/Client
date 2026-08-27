namespace VardyParty.HomeUi.Views;

/// <summary>
/// Face-on rest and catalog-handoff rules for the header crest turntable.
/// Catalog materialization must not abort a turn: aborting mid-spin is what
/// froze the logo at an edge-on angle when the first board painted.
/// </summary>
public static class BrandCrestSpin
{
    public const uint TurnMs = 1800;
    public const uint SettleMs = 480;
    public const double RestEpsilonDegrees = 1.5;

    /// <summary>
    /// A requested settle that has waited a full turn plus the ease plus
    /// slack is stuck (zombie turn after a layout-abort storm): snap to rest
    /// instead of animating again. Checked from animation callbacks and the
    /// deferred crest tick — never from an <c>IDispatcherTimer</c>, which
    /// Android TV starves under Choreographer load.
    /// </summary>
    public const uint SettleOverdueMs = TurnMs + SettleMs + 200;

    public static double NormalizeDegrees(double angle)
    {
        var n = angle % 360.0;
        return n < 0 ? n + 360.0 : n;
    }

    /// <summary>
    /// Nearest face-on rest. Turning the short way never crosses a full extra
    /// revolution; angles at or past 180 ease forward to 360 so the settle
    /// never reverses through the coin-edge. Exactly 180 — the edge-on freeze
    /// case — must also rest forward at 360: easing it back to 0 would swing
    /// the crest backward through the edge it froze on.
    /// </summary>
    public static double RestTargetDegrees(double current)
    {
        var n = NormalizeDegrees(current);
        return n < 180.0 ? 0.0 : 360.0;
    }

    public static bool IsFaceOnRest(double current, double epsilon = RestEpsilonDegrees)
    {
        var n = NormalizeDegrees(current);
        return n <= epsilon || n >= 360.0 - epsilon;
    }

    /// <summary>
    /// Keep the current 360° cycle running until settle is requested. The
    /// MAUI <c>repeat</c> callback is consulted at cycle end, so the crest
    /// finishes the turn it is on instead of freezing mid-rotation.
    /// </summary>
    public static bool ContinueSpinCycle(bool spinning, bool settleRequested) =>
        spinning && !settleRequested;

    /// <summary>
    /// When catalog layout cancels the spinner, settle from the last known
    /// angle immediately. If the spinner is still alive, wait for the cycle
    /// to finish (no AbortAnimation).
    /// </summary>
    public static bool SettleNowBecauseSpinDied(bool settleRequested, bool spinAnimationRunning) =>
        settleRequested && !spinAnimationRunning;
}
