namespace VardyParty.HomeUi.Views;

/// <summary>
/// Face-on rest and catalog-handoff rules for the header crest turntable.
/// Catalog materialization must not abort a turn: aborting mid-spin is what
/// froze the logo mid-turn when the first board painted.
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
    /// Nearest upright rest (0° or 360°). Turning the short way never crosses
    /// a full extra revolution; angles at or past 180 ease forward to 360 so
    /// the settle never reverses the in-plane turn. Exactly 180 — upside-down,
    /// the old mid-turn freeze — must also rest forward at 360: easing it
    /// back to 0 would spin the mark backward.
    /// </summary>
    public static double RestTargetDegrees(double current)
    {
        var n = NormalizeDegrees(current);
        return n < 180.0 ? 0.0 : 360.0;
    }

    /// <summary>
    /// Upright rest: the mark is at 0° or 360°. 180° is upside-down, not rest.
    /// </summary>
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
    /// One frame of the in-plane crest turn. Only
    /// <see cref="MarkRotationDegrees"/> follows the angle.
    /// </summary>
    public readonly record struct BrandCrestSpinPose(
        double MarkRotationDegrees,
        double BezelRotationY,
        double FaceScaleX,
        double SheenOpacity,
        bool DrivesSheen);

    /// <summary>
    /// In-plane pose for <paramref name="angleDegrees"/>. The mark rotation is
    /// the only value that changes. Bezel yaw and face scale stay at rest, and
    /// <see cref="BrandCrestSpinPose.DrivesSheen"/> is false: this frame does
    /// not paint the sheen. Gloss, sheen, and the disc (highlight and rim) are
    /// fixed overlays.
    /// </summary>
    public static BrandCrestSpinPose Pose(double angleDegrees) =>
        new(
            MarkRotationDegrees: angleDegrees,
            BezelRotationY: 0,
            FaceScaleX: 1,
            SheenOpacity: 0,
            DrivesSheen: false);
}
