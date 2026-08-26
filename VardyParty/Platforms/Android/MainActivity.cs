using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;

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
                // overlay can cancel resolution and close itself.
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
            // The single-page XAML homepage has no route stack: overlays and
            // the menu consume Back via the suppression flags above, so an
            // unhandled Back at the homepage exits the app.
            Log.Info("MainActivity", "[MAIN] Back at homepage - exiting app");
            FinishAndRemoveTask();
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
