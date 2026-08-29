using System.Reflection;
using Microsoft.Maui;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WinUiWindow = Microsoft.UI.Xaml.Window;
using WinUiVisibility = Microsoft.UI.Xaml.Visibility;

namespace VardyParty.Platforms.Windows;

internal static class WindowsWindowChrome
{
    /// <summary>
    /// Kill switch for bisecting startup crashes: set VARDYPARTY_NO_CHROME=1 —
    /// or create the flag file %LOCALAPPDATA%\VardyParty\flags\no-chrome, which
    /// also reaches packaged MSIX launches via shell:AppsFolder where terminal
    /// environment variables never arrive — to skip all custom window chrome
    /// (title-bar extension, collapse, drag regions) and run with stock WinUI
    /// chrome.
    /// </summary>
    public static bool IsChromeDisabled => ChromeDisabledTrigger != null;

    private const string ChromeVariableName = "VARDYPARTY_NO_CHROME";
    private const string ChromeFlagFileName = "no-chrome";

    /// <summary>Which mechanism disabled chrome (env var or flag file); null when chrome is on.</summary>
    private static string? ChromeDisabledTrigger { get; } = DetectChromeDisabledTrigger();

    private static bool _chromeDisabledLogged;

    private static string? DetectChromeDisabledTrigger()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(ChromeVariableName) == "1")
            {
                return $"environment variable {ChromeVariableName}=1";
            }
        }
        catch
        {
            // Reading the environment must never break startup.
        }

        var flagPath = VardyParty.Ports.StartupFlagFiles.Find(ChromeFlagFileName);
        return flagPath != null ? $"flag file {flagPath}" : null;
    }

    private static bool SkipChrome()
    {
        if (!IsChromeDisabled) return false;

        if (!_chromeDisabledLogged)
        {
            _chromeDisabledLogged = true;
            WindowsEventLogger.Info("WindowsWindowChrome", $"Custom window chrome disabled via {ChromeDisabledTrigger} — using stock WinUI chrome");
        }

        return true;
    }

    /// <summary>
    /// Must run before MAUI MapContent connects NavigationRootManager.
    /// Extending into the title bar avoids the classic Win32 caption strip that shows "VardyParty".
    /// Chrome failure must never prevent the window from showing: any error is logged
    /// and the window falls back to default chrome.
    /// </summary>
    public static void PrepareBeforeMauiConnect(WinUiWindow nativeWindow)
    {
        if (SkipChrome()) return;

        try
        {
            nativeWindow.ExtendsContentIntoTitleBar = true;
            nativeWindow.Title = string.Empty;

            if (GetAppWindow(nativeWindow)?.TitleBar is { } titleBar && AppWindowTitleBar.IsCustomizationSupported())
            {
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            }
        }
        catch (Exception ex)
        {
            WindowsEventLogger.Error("WindowsWindowChrome", "PrepareBeforeMauiConnect failed; falling back to default chrome", ex);
        }
    }

    /// <summary>
    /// Main games screen uses the in-app blue header only — no separate WinUI title bar.
    /// Never calls AppWindow.Show()/Window.Activate(): MAUI shows and activates its own
    /// window (MauiWinUIApplication.OnLaunched), and forcing it mid-content-connect
    /// destabilised startup on Windows App SDK 1.8. The OnActivated lifecycle hook
    /// re-applies this chrome after the first activation.
    /// </summary>
    public static void ApplyMainWindowChrome(WinUiWindow? nativeWindow, IMauiContext? mauiContext = null)
    {
        if (nativeWindow == null) return;
        if (SkipChrome()) return;

        try
        {
            mauiContext ??= nativeWindow.GetWindow()?.Handler?.MauiContext;

            PrepareBeforeMauiConnect(nativeWindow);

            if (nativeWindow.GetWindow() is Microsoft.Maui.Controls.Window mauiWindow)
            {
                mauiWindow.Title = string.Empty;
            }

            HideMauiNavigationTitleBar(mauiContext);

            void CollapseTitleBar()
            {
                try
                {
                    CollapseMauiTitleBar(nativeWindow.Content);
                }
                catch (Exception ex)
                {
                    WindowsEventLogger.Error("WindowsWindowChrome", "CollapseMauiTitleBar failed; keeping default title bar", ex);
                }
            }

            CollapseTitleBar();

            if (nativeWindow.Content is FrameworkElement { IsLoaded: false } root)
            {
                root.Loaded += (_, _) => CollapseTitleBar();
            }

            nativeWindow.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CollapseTitleBar);

            WindowsWindowDragHelper.EnableMainWindowDrag(nativeWindow);
        }
        catch (Exception ex)
        {
            WindowsEventLogger.Error("WindowsWindowChrome", "ApplyMainWindowChrome failed; falling back to default chrome", ex);
        }
    }

    static AppWindow? GetAppWindow(WinUiWindow nativeWindow) =>
        nativeWindow is MauiWinUIWindow mauiWinUiWindow
            ? mauiWinUiWindow.AppWindow
            : AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(nativeWindow)));

    static void HideMauiNavigationTitleBar(IMauiContext? mauiContext)
    {
        if (mauiContext == null) return;

        try
        {
            var extensionsType = typeof(Microsoft.Maui.Platform.WindowExtensions).Assembly.GetType("Microsoft.Maui.Platform.MauiContextExtensions");
            var getRootManager = extensionsType?.GetMethod(
                "GetNavigationRootManager",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var rootManager = getRootManager?.Invoke(null, [mauiContext]);
            if (rootManager == null) return;

            rootManager.GetType().GetMethod(
                    "SetTitleBarVisibility",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .Invoke(rootManager, [false]);

            rootManager.GetType().GetMethod(
                    "SetTitle",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .Invoke(rootManager, [string.Empty]);

            var rootView = rootManager.GetType().GetProperty("RootView")?.GetValue(rootManager);
            rootView?.GetType().GetMethod(
                    "UpdateAppTitleBar",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .Invoke(rootView, [0, false, new Microsoft.UI.Xaml.Thickness(0)]);
        }
        catch
        {
            // Best-effort; visual-tree collapse below still runs.
        }
    }

    static void CollapseMauiTitleBar(DependencyObject? root)
    {
        if (root == null) return;

        if (root is FrameworkElement element)
        {
            if (element.Name is "AppTitleBarContainer" or "AppTitleBarContentControl" or "AppTitleBar" or "AppTitle")
            {
                element.Visibility = WinUiVisibility.Collapsed;
                element.Height = 0;
                element.MinHeight = 0;
            }

            if (element is TextBlock { Text: { } text } &&
                text.Contains("VardyParty", StringComparison.OrdinalIgnoreCase))
            {
                element.Visibility = WinUiVisibility.Collapsed;
                element.Height = 0;
            }

            if (element is NavigationView navigationView)
            {
                navigationView.IsTitleBarAutoPaddingEnabled = false;
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            CollapseMauiTitleBar(VisualTreeHelper.GetChild(root, i));
        }
    }
}
