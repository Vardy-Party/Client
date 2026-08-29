using System;

namespace VardyParty.Desktop.Services;

/// <summary>
/// In-window Close chip reveal/hide policy. The chip stays hidden so video
/// can fill the window; it appears when the pointer is near the top-right
/// resting place or the playback surface is tapped. Auto-hides after
/// <see cref="AutoHideDelay"/> once the pointer leaves / touch goes idle.
///
/// AIRSPACE: the reserved height is a separate grid row ABOVE the native
/// libvlc child — collapsing to <see cref="HiddenReserveHeight"/> keeps a
/// thin invisible hit-zone so hover still works. Putting the chip on top
/// of the video would make it unclickable. Never a "Now Playing" banner.
/// </summary>
public enum DesktopCloseChipAction
{
    None,
    StartAutoHide,
    CancelAutoHide,
}

public sealed class DesktopCloseChipReveal
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

    public bool IsRevealed { get; private set; }

    public bool Hovering { get; private set; }

    public bool ChipVisible => IsRevealed;

    /// <summary>
    /// Forced chrome-row height. <see cref="double.NaN"/> means Auto (toast
    /// is up — let the row size to the toast, still no title banner).
    /// </summary>
    public double ReserveHeight(bool toastVisible)
    {
        if (toastVisible)
        {
            return double.NaN;
        }

        return IsRevealed ? RevealedReserveHeight : HiddenReserveHeight;
    }

    public double HitZoneHeight =>
        IsRevealed ? RevealedReserveHeight : HiddenReserveHeight;

    public void Reset()
    {
        IsRevealed = false;
        Hovering = false;
    }

    public DesktopCloseChipAction OnHoverEnter()
    {
        Hovering = true;
        IsRevealed = true;
        return DesktopCloseChipAction.CancelAutoHide;
    }

    public DesktopCloseChipAction OnHoverLeave()
    {
        Hovering = false;
        return IsRevealed ? DesktopCloseChipAction.StartAutoHide : DesktopCloseChipAction.None;
    }

    /// <summary>
    /// Any tap/touch on the playback surface. Reveals the chip; does not
    /// close. A later tap on the chip itself is the close path.
    /// </summary>
    public DesktopCloseChipAction OnTouched()
    {
        IsRevealed = true;
        return Hovering ? DesktopCloseChipAction.CancelAutoHide : DesktopCloseChipAction.StartAutoHide;
    }

    public DesktopCloseChipAction OnAutoHideElapsed()
    {
        if (Hovering)
        {
            return DesktopCloseChipAction.CancelAutoHide;
        }

        IsRevealed = false;
        return DesktopCloseChipAction.None;
    }

    /// <summary>
    /// Top-right rectangle in window coordinates (origin top-left).
    /// Generous in X (<see cref="HitZoneWidth"/>); Y follows the reserved
    /// strip so we never claim a hit on the native video child.
    /// </summary>
    public static bool IsNearRestingPlace(double x, double y, double windowWidth, bool revealed)
    {
        if (windowWidth <= 0 || x < 0 || y < 0)
        {
            return false;
        }

        var zoneHeight = revealed ? RevealedReserveHeight : HiddenReserveHeight;
        return x >= windowWidth - HitZoneWidth && x <= windowWidth && y <= zoneHeight;
    }
}
