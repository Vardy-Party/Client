using System;

namespace VardyParty.Linux.Services;

/// <summary>
/// In-window Close chip reveal/hide policy. The chip stays hidden so video
/// can fill the window; it appears when the pointer is near the top-right
/// resting place or the playback surface is tapped. Auto-hides after
/// <see cref="AutoHideDelay"/> once the pointer leaves / touch goes idle.
///
/// Overlay mode (<see cref="OverlayOnVideo"/>): the chip floats on the
/// composited picture and <see cref="ReserveHeight"/> returns NaN so the
/// page does not steal a strip. Legacy reserved-strip mode remains for
/// tests and the standalone-window companion panel if a host still wants
/// it. Never a "Now Playing" banner.
/// </summary>
public enum LinuxCloseChipAction
{
    None,
    StartAutoHide,
    CancelAutoHide,
}

public sealed class LinuxCloseChipReveal
{
    /// <summary>Idle hide after pointer leaves or a tap-to-reveal.</summary>
    public static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Thin top reserve while the chip is hidden — black, not a banner.
    /// Wide enough for a hover/touch hit-zone; short enough that the picture
    /// is effectively fullscreen-in-window.
    /// </summary>
    public const double HiddenReserveHeight = 24;

    /// <summary>Reserve while the chip is showing — one 36px chip + padding.</summary>
    public const double RevealedReserveHeight = 44;

    /// <summary>Generous top-right hit-zone (wider than the 36px chip).</summary>
    public const double HitZoneWidth = 168;

    /// <summary>
    /// Hover/tap zone height when the chip sits on the picture. Taller than
    /// the reserved-strip hit-zone so the pointer can find it over video.
    /// </summary>
    public const double OverlayHitZoneHeight = 80;

    /// <summary>
    /// True when chrome floats on the composited picture (no stolen strip).
    /// </summary>
    public bool OverlayOnVideo { get; set; }

    public bool IsRevealed { get; private set; }

    public bool Hovering { get; private set; }

    public bool ChipVisible => IsRevealed;

    /// <summary>
    /// Forced chrome-row height. <see cref="double.NaN"/> means Auto: overlay
    /// mode never steals a strip; reserved-strip mode also uses Auto when a
    /// toast is up so the row sizes to content (still no title banner).
    /// </summary>
    public double ReserveHeight(bool toastVisible)
    {
        if (OverlayOnVideo || toastVisible)
        {
            return double.NaN;
        }

        return IsRevealed ? RevealedReserveHeight : HiddenReserveHeight;
    }

    public double HitZoneHeight =>
        OverlayOnVideo
            ? OverlayHitZoneHeight
            : (IsRevealed ? RevealedReserveHeight : HiddenReserveHeight);

    public void Reset()
    {
        IsRevealed = false;
        Hovering = false;
    }

    public LinuxCloseChipAction OnHoverEnter()
    {
        Hovering = true;
        IsRevealed = true;
        return LinuxCloseChipAction.CancelAutoHide;
    }

    public LinuxCloseChipAction OnHoverLeave()
    {
        Hovering = false;
        return IsRevealed ? LinuxCloseChipAction.StartAutoHide : LinuxCloseChipAction.None;
    }

    /// <summary>
    /// Any tap/touch on the playback surface. Reveals the chip; does not
    /// close. A later tap on the chip itself is the close path.
    /// </summary>
    public LinuxCloseChipAction OnTouched()
    {
        IsRevealed = true;
        return Hovering ? LinuxCloseChipAction.CancelAutoHide : LinuxCloseChipAction.StartAutoHide;
    }

    public LinuxCloseChipAction OnAutoHideElapsed()
    {
        if (Hovering)
        {
            return LinuxCloseChipAction.CancelAutoHide;
        }

        IsRevealed = false;
        return LinuxCloseChipAction.None;
    }

    /// <summary>
    /// Top-right rectangle in window coordinates (origin top-left).
    /// Generous in X (<see cref="HitZoneWidth"/>). Overlay mode uses
    /// <see cref="OverlayHitZoneHeight"/> so hover works over the picture;
    /// reserved-strip mode keeps Y inside the stolen bar.
    /// </summary>
    public static bool IsNearRestingPlace(
        double x,
        double y,
        double windowWidth,
        bool revealed,
        bool overlayOnVideo = false)
    {
        if (windowWidth <= 0 || x < 0 || y < 0)
        {
            return false;
        }

        var zoneHeight = overlayOnVideo
            ? OverlayHitZoneHeight
            : (revealed ? RevealedReserveHeight : HiddenReserveHeight);
        return x >= windowWidth - HitZoneWidth && x <= windowWidth && y <= zoneHeight;
    }
}
