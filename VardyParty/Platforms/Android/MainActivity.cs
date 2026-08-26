using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using VardyParty.Presentation;

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
        private static bool _flyoutMenuOpen;

        /// <summary>
        /// Per-overlay Back suppression state, reported by <see cref="HomeHostPage"/>.
        /// Static (process lifetime), so it is reset in <see cref="OnCreate"/> —
        /// a stale flag from a previous activity instance must never suppress
        /// Back on a fresh idle homepage.
        /// </summary>
        public static OverlayBackSuppressionTracker OverlaySuppression { get; } = new();

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

            if (OverlaySuppression.IsSuppressed)
            {
                // Overlay is active and wants to consume Back. Dispatch to the remote handler so the
                // overlay can cancel resolution and close itself.
                Log.Info("MainActivity",
                    $"[MAIN] Back pressed while overlay visible ({OverlaySuppression.DescribeActive()}) - delegating to overlay handler");
                try
                {
                    if (RemoteKeyHandler.HandleKeyDown(Keycode.Back, null))
                    {
                        return;
                    }
                }
                catch { }

                // Fallback: if no handler consumed the event, cancel any stream switching state but do not navigate.
                OverlaySuppression.Reset();
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

            // Fresh activity: no overlay can be visible yet. Without this reset a
            // previous session that ended mid-overlay (device-code sign-in, stream
            // resolution) left the static suppression active on an idle homepage.
            OverlaySuppression.Reset();
            SetFlyoutMenuOpen(false);

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
            if (OverlaySuppression.IsSuppressed)
            {
                Log.Info("MainActivity",
                    $"[MAIN] Remote Back suppressed due to overlay: {OverlaySuppression.DescribeActive()}");
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
