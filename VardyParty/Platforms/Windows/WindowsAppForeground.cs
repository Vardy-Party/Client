#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using WinUiWindow = Microsoft.UI.Xaml.Window;

namespace VardyParty.Platforms.Windows;

/// <summary>
/// Bring the MAUI main window above the browser after Auth0 (or any) external flow.
/// Plain <see cref="WinUiWindow.Activate"/> is often ignored while the browser still
/// holds foreground focus — AttachThreadInput + SetForegroundWindow is required.
/// </summary>
internal static class WindowsAppForeground
{
    private const int SwRestore = 9;

    public static void BringMainWindowToFront()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(BringMainWindowToFrontCore);
        }
        catch
        {
            // Never fail the auth path because focus failed.
        }
    }

    private static void BringMainWindowToFrontCore()
    {
        try
        {
            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault();
            var native = mauiWindow?.Handler?.PlatformView as WinUiWindow
                ?? mauiWindow?.Handler?.PlatformView as MauiWinUIWindow;
            if (native == null)
                return;

            var hwnd = WindowNative.GetWindowHandle(native);
            if (hwnd == IntPtr.Zero)
                return;

            var appWindow = native.AppWindow;
            if (appWindow?.Presenter is OverlappedPresenter overlapped
                && overlapped.State == OverlappedPresenterState.Minimized)
            {
                overlapped.Restore();
            }

            try { appWindow?.Show(true); } catch { }
            try { appWindow?.MoveInZOrderAtTop(); } catch { }
            try { native.Activate(); } catch { }

            ForceForeground(hwnd);
        }
        catch (Exception ex)
        {
            WindowsEventLogger.Error("WindowsAppForeground", "BringMainWindowToFront failed", ex);
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
            return;

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }

        // Last resort: flash the taskbar if Windows still blocked the steal.
        if (GetForegroundWindow() != hwnd)
        {
            var flash = new FlashWInfo
            {
                cbSize = (uint)Marshal.SizeOf<FlashWInfo>(),
                hwnd = hwnd,
                dwFlags = FlashAll | FlashTimerNoFg,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref flash);
        }
    }

    private const uint FlashAll = 3;
    private const uint FlashTimerNoFg = 12;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWInfo pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
}
#endif
