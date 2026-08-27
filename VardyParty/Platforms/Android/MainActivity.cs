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

        /// <summary>
        /// Monotonic timestamp of the last Back an overlay consumed. Feeds
        /// <see cref="HomeBackDecision"/>'s exit grace: on the saturated TV
        /// main thread the menu closes long before the panel repaints, so a
        /// repeat Back against the stale frame must not exit the app.
        /// </summary>
        private static long _lastOverlayBackMs;

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

            switch (HomeBackDecision.Decide(OverlaySuppression.IsSuppressed, _lastOverlayBackMs, System.Environment.TickCount64))
            {
                case HomeBackDecision.BackAction.DelegateToOverlays:
                    // Overlay is active and wants to consume Back. Dispatch to the remote handler so the
                    // overlay can cancel resolution and close itself.
                    Log.Info("MainActivity",
                        $"[MAIN] Back pressed while overlay visible ({OverlaySuppression.DescribeActive()}) - delegating to overlay handler");
                    _lastOverlayBackMs = System.Environment.TickCount64;
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

                case HomeBackDecision.BackAction.IgnoreStaleExit:
                    Log.Info("MainActivity", "[MAIN] Back within exit grace of an overlay close - ignored");
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
            _lastOverlayBackMs = 0;

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

            // This handler is subscribed in OnCreate, BEFORE HomeHostPage's
            // overlay handler joins the multicast, so it always sees the
            // suppression state as it was when the user pressed Back — the
            // page handler closing the menu later in the same invocation
            // cannot turn this press into an app exit.
            switch (HomeBackDecision.Decide(OverlaySuppression.IsSuppressed, _lastOverlayBackMs, System.Environment.TickCount64))
            {
                case HomeBackDecision.BackAction.DelegateToOverlays:
                    // The overlay handler (later in this multicast) consumes it.
                    Log.Info("MainActivity",
                        $"[MAIN] Remote Back suppressed due to overlay: {OverlaySuppression.DescribeActive()}");
                    _lastOverlayBackMs = System.Environment.TickCount64;
                    return;

                case HomeBackDecision.BackAction.IgnoreStaleExit:
                    Log.Info("MainActivity", "[MAIN] Remote Back within exit grace of an overlay close - ignored");
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
