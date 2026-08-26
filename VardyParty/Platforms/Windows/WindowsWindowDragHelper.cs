using System.Runtime.InteropServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using WinUiWindow = Microsoft.UI.Xaml.Window;

namespace VardyParty.Platforms.Windows;

internal static class WindowsWindowDragHelper
{
    private const int VkLButton = 0x01;
    private const int HeaderHeightPx = 72;
    private const int HeaderRightInteractiveReservePx = 230;

    private static readonly HashSet<UIElement> AttachedPointerElements = [];
    private static readonly HashSet<nint> AttachedHeaderDragWindows = [];

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer;
    private static POINT _dragStartCursor;
    private static global::Windows.Graphics.PointInt32 _dragStartWindowPos;
    private static AppWindow? _activeDragAppWindow;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static void EnableMainWindowDrag(WinUiWindow? nativeWindow)
    {
        if (nativeWindow == null) return;

        EnableHeaderDragRegions(nativeWindow);
    }

    public static void AttachPointerDrag(
        UIElement element,
        WinUiWindow nativeWindow,
        Func<object?, UIElement, bool>? canStartDrag = null)
    {
        if (!AttachedPointerElements.Add(element)) return;

        var isDraggingWindow = false;
        POINT dragStartCursor = default;
        global::Windows.Graphics.PointInt32 dragStartWindowPos = default;

        element.PointerPressed += (_, e) =>
        {
            try
            {
                var point = e.GetCurrentPoint(element);
                if (!point.Properties.IsLeftButtonPressed) return;

                var appWindow = GetAppWindow(nativeWindow);
                if (appWindow == null) return;
                if (appWindow.Presenter?.Kind == AppWindowPresenterKind.FullScreen) return;
                if (canStartDrag != null && !canStartDrag(e.OriginalSource, element)) return;

                if (!GetCursorPos(out dragStartCursor)) return;
                dragStartWindowPos = appWindow.Position;
                isDraggingWindow = true;

                element.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            catch
            {
            }
        };

        element.PointerMoved += (_, e) =>
        {
            try
            {
                if (!isDraggingWindow) return;

                var appWindow = GetAppWindow(nativeWindow);
                if (appWindow == null) return;

                var point = e.GetCurrentPoint(element);
                if (!point.Properties.IsLeftButtonPressed)
                {
                    isDraggingWindow = false;
                    try { element.ReleasePointerCapture(e.Pointer); } catch { }
                    return;
                }

                if (!GetCursorPos(out var currentCursor)) return;

                var dx = currentCursor.X - dragStartCursor.X;
                var dy = currentCursor.Y - dragStartCursor.Y;

                appWindow.Move(new global::Windows.Graphics.PointInt32(
                    dragStartWindowPos.X + dx,
                    dragStartWindowPos.Y + dy));

                e.Handled = true;
            }
            catch
            {
            }
        };

        void EndWindowDrag(PointerRoutedEventArgs e)
        {
            if (!isDraggingWindow) return;
            isDraggingWindow = false;
            try { element.ReleasePointerCapture(e.Pointer); } catch { }
        }

        element.PointerReleased += (_, e) => EndWindowDrag(e);
        element.PointerCanceled += (_, e) => EndWindowDrag(e);
        element.PointerCaptureLost += (_, e) => EndWindowDrag(e);
    }

    public static void BeginPointerDrag(WinUiWindow nativeWindow)
    {
        nativeWindow.DispatcherQueue.TryEnqueue(() => BeginPointerDragCore(nativeWindow));
    }

    private static void BeginPointerDragCore(WinUiWindow nativeWindow)
    {
        var appWindow = GetAppWindow(nativeWindow);
        if (appWindow == null) return;
        if (appWindow.Presenter?.Kind == AppWindowPresenterKind.FullScreen) return;
        if (!GetCursorPos(out _dragStartCursor)) return;

        _dragStartWindowPos = appWindow.Position;
        _activeDragAppWindow = appWindow;

        _dragTimer?.Stop();
        _dragTimer = nativeWindow.DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dragTimer.Tick -= OnDragTimerTick;
        _dragTimer.Tick += OnDragTimerTick;
        _dragTimer.Start();
    }

    private static void OnDragTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_activeDragAppWindow == null)
        {
            sender.Stop();
            return;
        }

        if (!IsLeftButtonPressed())
        {
            _activeDragAppWindow = null;
            sender.Stop();
            return;
        }

        if (!GetCursorPos(out var currentCursor)) return;

        var dx = currentCursor.X - _dragStartCursor.X;
        var dy = currentCursor.Y - _dragStartCursor.Y;

        _activeDragAppWindow.Move(new global::Windows.Graphics.PointInt32(
            _dragStartWindowPos.X + dx,
            _dragStartWindowPos.Y + dy));
    }

    private static void EnableHeaderDragRegions(WinUiWindow nativeWindow)
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(nativeWindow);
            if (!AttachedHeaderDragWindows.Add(handle)) return;

            var appWindow = GetAppWindow(nativeWindow);
            if (appWindow?.TitleBar is not { } titleBar || !AppWindowTitleBar.IsCustomizationSupported())
                return;

            void UpdateDragRects()
            {
                try
                {
                    var width = appWindow.Size.Width;
                    if (width <= HeaderRightInteractiveReservePx)
                    {
                        SetCaptionDragRegions(appWindow, titleBar, []);
                        return;
                    }

                    var leftWidth = Math.Min(300, width - HeaderRightInteractiveReservePx);
                    var centerLeft = leftWidth;
                    var centerWidth = width - HeaderRightInteractiveReservePx - centerLeft;

                    SetCaptionDragRegions(appWindow, titleBar,
                    [
                        new global::Windows.Graphics.RectInt32(0, 0, leftWidth, HeaderHeightPx),
                        new global::Windows.Graphics.RectInt32(centerLeft, 0, centerWidth, HeaderHeightPx)
                    ]);
                }
                catch (Exception ex)
                {
                    WindowsEventLogger.Error("WindowsWindowDragHelper", "Updating header drag regions failed; window stays draggable via default chrome", ex);
                }
            }

            appWindow.Changed += (_, e) =>
            {
                if (e.DidSizeChange) UpdateDragRects();
            };

            if (nativeWindow.Content is FrameworkElement { IsLoaded: false } root)
                root.Loaded += (_, _) => UpdateDragRects();

            nativeWindow.DispatcherQueue.TryEnqueue(UpdateDragRects);
            UpdateDragRects();
        }
        catch (Exception ex)
        {
            WindowsEventLogger.Error("WindowsWindowDragHelper", "EnableHeaderDragRegions failed; skipping custom drag regions", ex);
        }
    }

    private static bool _captionRegionApiFallbackLogged;

    /// <summary>
    /// Windows App SDK 1.8 deprecates AppWindowTitleBar.SetDragRectangles; the supported
    /// API is InputNonClientPointerSource.SetRegionRects with NonClientRegionKind.Caption
    /// (both take physical pixels). The old call is kept as a logged fallback for runtimes
    /// where the newer input API is unavailable.
    /// </summary>
    private static void SetCaptionDragRegions(
        AppWindow appWindow,
        AppWindowTitleBar titleBar,
        global::Windows.Graphics.RectInt32[] rects)
    {
        try
        {
            var nonClientSource = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(appWindow.Id);
            if (nonClientSource != null)
            {
                nonClientSource.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, rects);
                return;
            }
        }
        catch (Exception ex)
        {
            if (!_captionRegionApiFallbackLogged)
            {
                _captionRegionApiFallbackLogged = true;
                WindowsEventLogger.Warning("WindowsWindowDragHelper", "InputNonClientPointerSource unavailable; falling back to AppWindowTitleBar.SetDragRectangles", ex);
            }
        }

        titleBar.SetDragRectangles(rects);
    }

    private static bool IsLeftButtonPressed() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    private static AppWindow? GetAppWindow(WinUiWindow nativeWindow) =>
        nativeWindow is MauiWinUIWindow mauiWinUiWindow
            ? mauiWinUiWindow.AppWindow
            : AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(nativeWindow)));
}
