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

    public const double RimMinScaleX = 0.06;
    public const double RimMaxScaleX = 0.34;
    public const double RimDriftPx = 7.0;

    /// <summary>
    /// Fixed camera lean so the gold rim stays visible at every yaw. A pure
    /// <c>RotationY</c> coin-flip goes face-on twice per turn and depth
    /// vanishes; this tilt keeps thickness on for the whole turntable.
    /// </summary>
    public const double CameraTiltXDegrees = -18.0;

    /// <summary>
    /// Floor for edge-rim opacity so depth never blinks off at face-on yaw.
    /// </summary>
    public const double RimOpacityFloor = 0.42;

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

    /// <summary>
    /// Face-on rest: the crest is at 0° or 360°. 180° is edge-on, not rest.
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
    /// One frame of the tilted turntable. <see cref="FaceRotationX"/> is the
    /// fixed camera lean; <see cref="FaceRotationY"/> is yaw; crest faces use
    /// <c>|cos|</c> scale with front/back opacity. Edge rim stays at least
    /// <see cref="RimOpacityFloor"/> so depth never drops out.
    /// </summary>
    public readonly record struct BrandCrestSpinPose(
        double MarkRotationDegrees,
        double FaceRotationX,
        double FaceRotationY,
        double FaceScaleX,
        double FrontOpacity,
        double BackOpacity,
        double EdgeScaleX,
        double EdgeTranslationX,
        double EdgeOpacity,
        double SheenOpacity,
        double SheenTravelFraction,
        bool DrivesSheen);

    /// <summary>
    /// Turntable pose for yaw <paramref name="angleDegrees"/>. Persistent
    /// tilt keeps disc thickness visible; crest on both faces; sheen glints
    /// with the yaw so direction stays readable.
    /// </summary>
    public static BrandCrestSpinPose Pose(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var sin = Math.Sin(radians);
        var cos = Math.Cos(radians);
        var absSin = Math.Abs(sin);
        var absCos = Math.Abs(cos);
        var faceVisibility = Math.Clamp(absCos, 0.08, 1.0);
        var glint = Math.Abs(Math.Sin(2 * radians));
        var frontFacing = cos >= 0.0;
        // Tilt keeps a foreshortened rim even at yaw 0 / 180.
        var rimFromYaw = 0.9 * absSin;
        var rimFromTilt = RimOpacityFloor;

        return new(
            MarkRotationDegrees: 0,
            FaceRotationX: CameraTiltXDegrees,
            FaceRotationY: angleDegrees,
            FaceScaleX: absCos,
            FrontOpacity: frontFacing ? faceVisibility : 0.0,
            BackOpacity: frontFacing ? 0.0 : faceVisibility,
            EdgeScaleX: RimMinScaleX + (RimMaxScaleX - RimMinScaleX) * Math.Max(absSin, 0.35),
            EdgeTranslationX: sin * RimDriftPx,
            EdgeOpacity: Math.Max(rimFromTilt, rimFromYaw),
            SheenOpacity: absCos * (0.12 + 0.68 * glint * glint * glint),
            SheenTravelFraction: (NormalizeDegrees(angleDegrees) % 180.0) / 180.0,
            DrivesSheen: true);
    }
}
