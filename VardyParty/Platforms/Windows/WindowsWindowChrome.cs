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
    /// Must run before MAUI MapContent connects NavigationRootManager.
    /// Extending into the title bar avoids the classic Win32 caption strip that shows "VardyParty".
    /// </summary>
    public static void PrepareBeforeMauiConnect(WinUiWindow nativeWindow)
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

    /// <summary>
    /// Main games screen uses the in-app blue header only — no separate WinUI title bar.
    /// </summary>
    public static void ApplyMainWindowChrome(WinUiWindow? nativeWindow, IMauiContext? mauiContext = null)
    {
        if (nativeWindow == null) return;

        mauiContext ??= nativeWindow.GetWindow()?.Handler?.MauiContext;

        PrepareBeforeMauiConnect(nativeWindow);
        GetAppWindow(nativeWindow)?.Show();
        nativeWindow.Activate();

        if (nativeWindow.GetWindow() is Microsoft.Maui.Controls.Window mauiWindow)
        {
            mauiWindow.Title = string.Empty;
        }

        HideMauiNavigationTitleBar(mauiContext);

        void CollapseTitleBar() => CollapseMauiTitleBar(nativeWindow.Content);

        CollapseTitleBar();

        if (nativeWindow.Content is FrameworkElement { IsLoaded: false } root)
        {
            root.Loaded += (_, _) => CollapseTitleBar();
        }

        nativeWindow.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CollapseTitleBar);

        WindowsWindowDragHelper.EnableMainWindowDrag(nativeWindow, MainPage.Instance?.BlazorWebView);
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
