namespace VardyParty.Presentation;

/// <summary>A window rectangle in screen pixels (the units AppWindow uses).</summary>
public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Where a desktop window is: restored (normal), maximized, minimized, full screen, or another presenter.</summary>
public enum WindowPlacementMode
{
    Restored,
    Maximized,
    Minimized,
    FullScreen,
    Other,
}

/// <summary>What to do after the window left full screen: move/resize to <see cref="Bounds"/>, then maximize if asked.</summary>
public sealed record FullScreenRestorePlan(WindowRect? Bounds, bool Maximize);

/// <summary>
/// Puts a desktop window back where it was after full screen (Jon, 13 Nov 2026: "restore from
/// full-screen on windows by double click.... window will full screen dimensions").
/// <para>
/// Windows App SDK's FullScreen presenter does not remember the window's size: leaving it keeps
/// the full-screen rectangle, and a MoveAndResize issued while the presenter is still switching
/// is dropped. So this tracker watches every window change: it remembers the last restored
/// (normal) bounds and whether the window was maximized when it went full screen, and once the
/// window is out of full screen it hands out a plan until the window really is back (restored
/// at those bounds, or maximized), at most <see cref="MaxApplyAttempts"/> times. Works for every
/// way out of full screen (double-click, Esc, transport controls) and across repeated toggles.
/// Bounds are fitted into the work area of the display nearest to them, so a window whose
/// monitor was unplugged or changed resolution comes back on screen. Units are physical
/// pixels throughout, as AppWindow reports and takes them, so DPI does not need converting.
/// </para>
/// </summary>
public sealed class FullScreenRestoreTracker
{
    public const int MaxApplyAttempts = 3;

    private WindowRect? _restoredBounds;
    private bool _maximizedBeforeFullScreen;
    private WindowPlacementMode _mode = WindowPlacementMode.Restored;
    private FullScreenRestorePlan? _pending;
    private int _attempts;

    /// <summary>Last bounds seen while the window was restored (not maximized or full screen).</summary>
    public WindowRect? RestoredBounds => _restoredBounds;

    /// <summary>The plan still being applied after leaving full screen, if any.</summary>
    public FullScreenRestorePlan? Pending => _pending;

    /// <summary>
    /// Feed the window's current state (on start and on every presenter/size/position change).
    /// Returns a plan to apply now, or null when nothing needs doing.
    /// </summary>
    /// <param name="workAreaFor">Work area of the display nearest a rectangle; null skips fitting.</param>
    public FullScreenRestorePlan? Observe(
        WindowPlacementMode mode,
        WindowRect bounds,
        Func<WindowRect, WindowRect?>? workAreaFor = null)
    {
        var previous = _mode;
        _mode = mode;

        if (mode == WindowPlacementMode.FullScreen)
        {
            if (previous != WindowPlacementMode.FullScreen)
                _maximizedBeforeFullScreen = previous == WindowPlacementMode.Maximized;
            _pending = null;
            return null;
        }

        if (previous == WindowPlacementMode.FullScreen && mode != WindowPlacementMode.Minimized)
        {
            var target = _restoredBounds is { } saved ? Fit(saved, workAreaFor?.Invoke(saved)) : (WindowRect?)null;
            _pending = target is null && !_maximizedBeforeFullScreen
                ? null
                : new FullScreenRestorePlan(target, _maximizedBeforeFullScreen);
            _attempts = 0;
        }

        if (_pending is { } plan)
        {
            if (IsSatisfied(plan, mode, bounds))
            {
                _pending = null;
                if (mode == WindowPlacementMode.Restored && !bounds.IsEmpty)
                    _restoredBounds = bounds;
                return null;
            }

            if (mode == WindowPlacementMode.Minimized)
                return null;
            if (_attempts >= MaxApplyAttempts)
            {
                // Give up rather than fight the user or the shell; remember nothing from
                // the full-screen-sized rectangle.
                _pending = null;
                return null;
            }

            _attempts++;
            return plan;
        }

        if (mode == WindowPlacementMode.Restored && !bounds.IsEmpty)
            _restoredBounds = bounds;
        return null;
    }

    /// <summary>
    /// Fits <paramref name="bounds"/> into <paramref name="workArea"/>: no larger than it, and
    /// moved inside it. Unchanged when it already fits or there is no work area.
    /// </summary>
    public static WindowRect Fit(WindowRect bounds, WindowRect? workArea)
    {
        if (workArea is not { IsEmpty: false } area)
            return bounds;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.X, area.X, area.X + area.Width - width);
        var y = Math.Clamp(bounds.Y, area.Y, area.Y + area.Height - height);
        return new WindowRect(x, y, width, height);
    }

    private static bool IsSatisfied(FullScreenRestorePlan plan, WindowPlacementMode mode, WindowRect bounds)
    {
        if (plan.Maximize)
            return mode == WindowPlacementMode.Maximized;
        return mode == WindowPlacementMode.Restored && (plan.Bounds is not { } target || Near(bounds, target));
    }

    /// <summary>Within 2px per edge: scaling can round a requested size by a pixel.</summary>
    private static bool Near(WindowRect a, WindowRect b)
    {
        const int tolerance = 2;
        return Math.Abs(a.X - b.X) <= tolerance
            && Math.Abs(a.Y - b.Y) <= tolerance
            && Math.Abs(a.Width - b.Width) <= tolerance
            && Math.Abs(a.Height - b.Height) <= tolerance;
    }
}