using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using VardyParty.Presentation;
using Windows.Graphics;

namespace VardyParty.Platforms.Windows;

/// <summary>
/// Full-screen toggle for the app window that puts the window back exactly where it was.
/// Build 251: double-clicking out of full screen left the window at full-screen dimensions -
/// the old code queued one MoveAndResize right after SetPresenter(Default), which ran before
/// the presenter switch finished and was overwritten by the full-screen rectangle; the saved
/// bounds also lived in a player session, so leaving full screen any other way lost them.
/// <para>
/// One instance per AppWindow (outlives player sessions). It watches AppWindow.Changed and feeds
/// <see cref="FullScreenRestoreTracker"/>, which remembers the restored bounds and maximized
/// state and keeps asking for the restore until the window is really back. Bounds are physical
/// pixels and are fitted into the work area of the nearest display (DPI / monitor changes).
/// </para>
/// </summary>
internal sealed class WindowsFullScreenRestorer
{
    private static readonly ConditionalWeakTable<AppWindow, WindowsFullScreenRestorer> Instances = new();

    private readonly AppWindow _window;
    private readonly DispatcherQueue? _queue;
    private readonly Action<string, Exception> _logIgnored;
    private readonly FullScreenRestoreTracker _tracker = new();
    private bool _applying;
    private DispatcherQueueTimer? _verifyTimer;

    private WindowsFullScreenRestorer(AppWindow window, DispatcherQueue? queue, Action<string, Exception> logIgnored)
    {
        _window = window;
        _queue = queue;
        _logIgnored = logIgnored;
        Observe();
        _window.Changed += OnChanged;
    }

    public static WindowsFullScreenRestorer For(AppWindow window, DispatcherQueue? queue, Action<string, Exception> logIgnored)
        => Instances.GetValue(window, w => new WindowsFullScreenRestorer(w, queue ?? DispatcherQueue.GetForCurrentThread(), logIgnored));

    public bool IsFullScreen => _window.Presenter?.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>Enter full screen, or leave it and restore the previous size, position and maximized state.</summary>
    public void Toggle()
    {
        // Record the state now in case a change event has not arrived yet.
        Observe();
        // Default re-applies the window's own OverlappedPresenter (not a fresh one), so a
        // maximized window can go full screen directly - no Restore() first.
        _window.SetPresenter(IsFullScreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_applying) return;
        if (args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange)
            Observe();
    }

    private void Observe()
    {
        try
        {
            var plan = _tracker.Observe(CurrentMode(), CurrentBounds(), WorkAreaFor);
            if (plan is null) return;
            // Never apply inside the Changed callback / presenter switch - it gets dropped.
            if (_queue is null || !_queue.TryEnqueue(DispatcherQueuePriority.Low, ApplyPending))
                ApplyPending();
        }
        catch (Exception ex)
        {
            _logIgnored("FullScreenRestore.Observe", ex);
        }
    }

    private void ApplyPending()
    {
        if (_tracker.Pending is not { } plan || IsFullScreen) return;
        _applying = true;
        try
        {
            if (_window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } maximized)
                maximized.Restore();
            if (plan.Bounds is { } b)
                _window.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
            if (plan.Maximize && _window.Presenter is OverlappedPresenter overlapped)
                overlapped.Maximize();
        }
        catch (Exception ex)
        {
            _logIgnored("FullScreenRestore.Apply", ex);
        }
        finally
        {
            _applying = false;
        }

        // Check the result now, and once more after the shell has settled (a late
        // full-screen-sized WM_SIZE can still arrive without a change we observe).
        Observe();
        ScheduleVerify();
    }

    private void ScheduleVerify()
    {
        if (_queue is null || _tracker.Pending is null && _verifyTimer is null) return;
        try
        {
            _verifyTimer ??= CreateVerifyTimer(_queue);
            _verifyTimer.Stop();
            _verifyTimer.Start();
        }
        catch (Exception ex)
        {
            _logIgnored("FullScreenRestore.Verify", ex);
        }
    }

    private DispatcherQueueTimer CreateVerifyTimer(DispatcherQueue queue)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Observe();
        return timer;
    }

    private WindowPlacementMode CurrentMode() => _window.Presenter switch
    {
        { Kind: AppWindowPresenterKind.FullScreen } => WindowPlacementMode.FullScreen,
        OverlappedPresenter { State: OverlappedPresenterState.Maximized } => WindowPlacementMode.Maximized,
        OverlappedPresenter { State: OverlappedPresenterState.Minimized } => WindowPlacementMode.Minimized,
        OverlappedPresenter => WindowPlacementMode.Restored,
        _ => WindowPlacementMode.Other,
    };

    private WindowRect CurrentBounds()
    {
        var position = _window.Position;
        var size = _window.Size;
        return new WindowRect(position.X, position.Y, size.Width, size.Height);
    }

    private static WindowRect? WorkAreaFor(WindowRect bounds)
    {
        var display = DisplayArea.GetFromRect(
            new RectInt32(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)),
            DisplayAreaFallback.Nearest);
        if (display is null) return null;
        var area = display.WorkArea;
        return new WindowRect(area.X, area.Y, area.Width, area.Height);
    }
}