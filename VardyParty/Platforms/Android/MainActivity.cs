using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Microsoft.AspNetCore.Components;
using VardyParty.Kernel;

namespace VardyParty
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        Exported = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { Android.Content.Intent.ActionMain }, Categories = new[] { Android.Content.Intent.CategoryLauncher, Android.Content.Intent.CategoryLeanbackLauncher })]
    public class MainActivity : MauiAppCompatActivity
    {
        private static bool _overlayBackSuppression;
        private static bool _flyoutMenuOpen;

        public static void SetOverlayBackSuppression(bool suppress)
        {
            _overlayBackSuppression = suppress;
        }

        public static void SetFlyoutMenuOpen(bool open)
        {
            _flyoutMenuOpen = open;
        }

        public static bool IsFlyoutMenuOpen => _flyoutMenuOpen;
        private static Platforms.Android.RemoteKeyHandler? _remoteKeyHandler;

        public static Platforms.Android.RemoteKeyHandler RemoteKeyHandler
        {
            get
            {
                if (_remoteKeyHandler == null)
                {
                    _remoteKeyHandler = new Platforms.Android.RemoteKeyHandler();
                }
                return _remoteKeyHandler;
            }
        }

        public override void OnBackPressed()
        {
            Log.Info("MainActivity", "[MAIN] OnBackPressed");
            if (_flyoutMenuOpen)
            {
                Log.Info("MainActivity", "[MAIN] Back pressed while flyout open - closing menu");
                try
                {
                    if (RemoteKeyHandler.HandleKeyDown(Keycode.Back, null))
                    {
                        return;
                    }
                }
                catch { }

                SetFlyoutMenuOpen(false);
                return;
            }

            if (_overlayBackSuppression)
            {
                // Overlay is active and wants to consume Back. Dispatch to the remote handler so the
                // Blazor overlay can cancel resolution and close itself.
                Log.Info("MainActivity", "[MAIN] Back pressed while overlay visible - delegating to overlay handler");
                try
                {
                    if (RemoteKeyHandler.HandleKeyDown(Keycode.Back, null))
                    {
                        return;
                    }
                }
                catch { }

                // Fallback: if no handler consumed the event, cancel any stream switching state but do not navigate.
                _overlayBackSuppression = false;
                try
                {
                    var services = IPlatformApplication.Current?.Services;
                    var switching = services?.GetService(typeof(VardyParty.Ports.IStreamSwitchingService)) as VardyParty.Ports.IStreamSwitchingService;
                    switching?.Cleanup();
                }
                catch { }
                return;
            }

            HandleNavigationBack();
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Log.Info("MainActivity", "[MAIN] OnCreate wiring handlers");

            // Wire hardware back to logical navigation
            RemoteKeyHandler.OnBack -= RemoteBackHandler;
            RemoteKeyHandler.OnBack += RemoteBackHandler;

            // Wire Stop to logical navigation (return to Streams/Games depending on current)
            RemoteKeyHandler.OnStop -= RemoteStopHandler;
            RemoteKeyHandler.OnStop += RemoteStopHandler;
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (!_overlayBackSuppression)
            {
                MainPage.Instance?.RestoreWebViewFocus();
            }
        }

        protected override void OnDestroy()
        {
            RemoteKeyHandler.OnBack -= RemoteBackHandler;
            RemoteKeyHandler.OnStop -= RemoteStopHandler;
            base.OnDestroy();
        }

        private void RemoteBackHandler(Keycode keyCode)
        {
            if (_flyoutMenuOpen)
            {
                Log.Info("MainActivity", "[MAIN] Remote Back suppressed due to flyout menu");
                return;
            }

            // If an overlay (e.g., stream discovery) is active and intends to consume Back,
            // do not perform navigation here; let the overlay handler handle cancelation.
            if (_overlayBackSuppression)
            {
                Log.Info("MainActivity", "[MAIN] Remote Back suppressed due to overlay");
                return;
            }
            HandleNavigationBack();
        }

        private void RemoteStopHandler(Keycode keyCode)
        {
            HandleNavigationBack();
        }

        private void HandleNavigationBack()
        {
            Log.Info("MainActivity", "[MAIN] HandleNavigationBack called");
            var services = IPlatformApplication.Current?.Services;
            var navigation = services?.GetService<NavigationManager>();
            var selection = services?.GetService<SelectionState>();
            var mainPage = MainPage.Instance;

            if (navigation == null || mainPage == null)
            {
                Log.Info("MainActivity", "[MAIN] Back: no navigation/mainPage, exiting app");
                FinishAndRemoveTask();
                return;
            }

            Uri? uri;
            try
            {
                uri = navigation.ToAbsoluteUri(navigation.Uri);
            }
            catch (InvalidOperationException ex)
            {
                Log.Warn("MainActivity", $"[MAIN] Navigation not initialized: {ex.Message}; staying on Home");
                return;
            }

            Log.Info("MainActivity", $"[MAIN] Current URI: {uri}");
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            var targetRoute = ComputeParentRoute(segments, selection);
            Log.Info("MainActivity", $"[MAIN] Computed target route: '{targetRoute}'");

            if (string.IsNullOrWhiteSpace(targetRoute))
            {
                Log.Info("MainActivity", $"[MAIN] Back: current={uri.AbsolutePath} target=null -> exit app");
                FinishAndRemoveTask();
                return;
            }

            if (uri.AbsolutePath == "/" && targetRoute == "/")
            {
                Log.Info("MainActivity", "[MAIN] Back: already at root, exiting app");
                FinishAndRemoveTask();
                return;
            }

            Log.Info("MainActivity", $"[MAIN] Back: current={uri.AbsolutePath} target={targetRoute}");
            mainPage.NavigateToRoute(targetRoute);
        }

        private static string? ComputeParentRoute(IReadOnlyList<string> segments, SelectionState? selection)
        {
            if (segments.Count == 0)
            {
                if (selection != null) selection.LastRoute = "/";
                return "/";
            }

            if (segments[0].Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Count >= 4)
                {
                    var league = Uri.UnescapeDataString(segments[1]);
                    var home = Uri.UnescapeDataString(segments[2]);
                    var away = Uri.UnescapeDataString(segments[3]);
                    if (selection != null)
                    {
                        selection.LastLeague = league;
                        selection.LastHomeTeam = home;
                        selection.LastAwayTeam = away;
                        selection.LastRoute = "/";
                    }
                    return "/";
                }

                return "/";
            }

            if (segments[0].Equals("streams", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Count >= 2)
                {
                    var league = Uri.UnescapeDataString(segments[1]);
                    var target = $"/games/{Uri.EscapeDataString(league)}";
                    if (selection != null)
                    {
                        selection.LastLeague = league;
                        selection.LastRoute = target;
                    }
                    return target;
                }

                return "/";
            }

            if (segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
            {
                if (selection != null)
                {
                    selection.LastRoute = "/";
                }
                return "/";
            }

            // Unknown route -> exit
            return null;
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
        {
            if (_remoteKeyHandler != null && _remoteKeyHandler.HandleKeyDown(keyCode, e))
            {
                return true;
            }
            return base.OnKeyDown(keyCode, e);
        }

        public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
        {
            if (_remoteKeyHandler != null && _remoteKeyHandler.HandleKeyUp(keyCode, e))
            {
                return true;
            }
            return base.OnKeyUp(keyCode, e);
        }
    }
}
