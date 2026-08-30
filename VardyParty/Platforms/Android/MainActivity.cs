using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using VardyParty.Presentation;

namespace VardyParty
{
    [Activity(
        Theme = "@style/VardyParty.SplashTheme",
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

        /// <summary>
        /// Called when an overlay handler has consumed Back (e.g. cancel
        /// finding-streams). Arms exit grace so a repeat press against a
        /// stale frame cannot exit the app.
        /// </summary>
        public static void NoteOverlayBackConsumed() =>
            _lastOverlayBackMs = System.Environment.TickCount64;

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

            // Ask the live page first — OnBackPressed's ExitApp path never
            // reaches the OnBack multicast, and OverlaySuppression can lag
            // behind the finding-streams modal by a frame.
            if (TryConsumeHomeOverlayBack())
            {
                NoteOverlayBackConsumed();
                return;
            }

            switch (HomeBackDecision.Decide(OverlaySuppression.IsSuppressed, _lastOverlayBackMs, System.Environment.TickCount64))
            {
                case HomeBackDecision.BackAction.DelegateToOverlays:
                    // Tracker still set but page reported nothing open — still
                    // do not exit (stale paint / race). Never Reset() here:
                    // wiping suppression mid-session made the next Back exit.
                    Log.Info("MainActivity",
                        $"[MAIN] Back pressed while overlay flag set ({OverlaySuppression.DescribeActive()}) - not exiting");
                    NoteOverlayBackConsumed();
                    try
                    {
                        if (RemoteKeyHandler.HandleKeyDown(Keycode.Back, null))
                        {
                            return;
                        }
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

            ShowPhoneSplashOverlay();

            // Tell the system the cold-start path has produced a frame. Without
            // this, multi-second first-layout Daveys on the 32-bit TV look like
            // an ANR and Leanback kicks back to the Android home.
            Window?.DecorView?.Post(() =>
            {
                try
                {
                    ReportFullyDrawn();
                    Log.Info("MainActivity", "[MAIN] ReportFullyDrawn");
                }
                catch (Exception ex)
                {
                    Log.Warn("MainActivity", $"[MAIN] ReportFullyDrawn failed: {ex.Message}");
                }

                SchedulePhoneSplashDismiss();
            });
        }

        protected override void OnDestroy()
        {
            DismissPhoneSplashOverlay();
            RemoteKeyHandler.OnBack -= RemoteBackHandler;
            RemoteKeyHandler.OnStop -= RemoteStopHandler;
            base.OnDestroy();
        }

        /// <summary>
        /// Android 12+ phones only show a 108dp circular system splash. Our
        /// splash art is a 512px sheet (ball + version/commit); the circle
        /// samples the empty middle and looks like solid brand blue. TV uses
        /// the pre-31 full window drawable, so it already looks right.
        /// Cover the phone window with that same art until the homepage is up.
        /// </summary>
        private const int PhoneSplashMinMs = 1800;
        private global::Android.Views.View? _phoneSplashOverlay;
        private long _phoneSplashShownAtMs;

        private void ShowPhoneSplashOverlay()
        {
            if (MauiProgram.IsTv)
            {
                return;
            }

            try
            {
                var drawableId = Resources?.GetIdentifier(
                    "vardyparty_splash_generated", "drawable", PackageName) ?? 0;
                if (drawableId == 0)
                {
                    Log.Warn("MainActivity", "[MAIN] Phone splash drawable missing");
                    return;
                }

                var root = new FrameLayout(this);
                root.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#003090"));
                var image = new ImageView(this);
                image.SetScaleType(ImageView.ScaleType.FitCenter);
                image.SetImageResource(drawableId);
                root.AddView(image, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                AddContentView(root, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                _phoneSplashOverlay = root;
                _phoneSplashShownAtMs = Environment.TickCount64;
            }
            catch (Exception ex)
            {
                Log.Warn("MainActivity", $"[MAIN] Phone splash overlay failed: {ex.Message}");
            }
        }

        private void SchedulePhoneSplashDismiss()
        {
            var overlay = _phoneSplashOverlay;
            if (overlay is null)
            {
                return;
            }

            var remain = Math.Max(0, PhoneSplashMinMs - (int)(Environment.TickCount64 - _phoneSplashShownAtMs));
            overlay.PostDelayed(new Java.Lang.Runnable(DismissPhoneSplashOverlay), remain);
        }

        private void DismissPhoneSplashOverlay()
        {
            try
            {
                if (_phoneSplashOverlay?.Parent is ViewGroup parent)
                {
                    parent.RemoveView(_phoneSplashOverlay);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("MainActivity", $"[MAIN] Phone splash dismiss failed: {ex.Message}");
            }

            _phoneSplashOverlay = null;
        }

        private void RemoteBackHandler(Keycode keyCode)
        {
            // Prefer live page overlays (menu close) over exit. Same gate as
            // OnKeyDown / OnBackPressed — RemoteKeyHandler invokes this first
            // in the OnBack multicast.
            if (TryConsumeHomeOverlayBack())
            {
                NoteOverlayBackConsumed();
                return;
            }

            if (_flyoutMenuOpen)
            {
                Log.Info("MainActivity", "[MAIN] Remote Back suppressed due to flyout menu");
                return;
            }

            switch (HomeBackDecision.Decide(OverlaySuppression.IsSuppressed, _lastOverlayBackMs, System.Environment.TickCount64))
            {
                case HomeBackDecision.BackAction.DelegateToOverlays:
                    Log.Info("MainActivity",
                        $"[MAIN] Remote Back suppressed due to overlay: {OverlaySuppression.DescribeActive()}");
                    // Tracker said something is open but the page did not consume —
                    // try once more, then never exit.
                    TryConsumeHomeOverlayBack();
                    NoteOverlayBackConsumed();
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

        /// <summary>
        /// Route Back to <see cref="HomeHostPage"/> overlays (menu, device
        /// code, finding-streams) using live page state — not only the static
        /// suppression tracker.
        /// </summary>
        private static bool TryConsumeHomeOverlayBack()
        {
            try
            {
                var page = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault()?.Page;
                if (page is HomeHostPage home)
                {
                    return home.TryHandleHardwareBack();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("MainActivity", $"[MAIN] TryConsumeHomeOverlayBack failed: {ex.Message}");
            }

            return false;
        }

        private void HandleNavigationBack()
        {
            if (TryConsumeHomeOverlayBack())
            {
                NoteOverlayBackConsumed();
                return;
            }

            // Last-chance guard against a stale Decide() that raced an overlay
            // paint — never FinishAndRemoveTask while suppression is set.
            if (OverlaySuppression.IsSuppressed)
            {
                Log.Info("MainActivity",
                    $"[MAIN] Back exit aborted — overlay still active ({OverlaySuppression.DescribeActive()})");
                NoteOverlayBackConsumed();
                return;
            }

            // The single-page XAML homepage has no route stack: overlays and
            // the menu consume Back via the suppression flags above, so an
            // unhandled Back at the homepage exits the app.
            Log.Info("MainActivity", "[MAIN] Back at homepage - exiting app");
            FinishAndRemoveTask();
        }

        /// <summary>
        /// Single owner for TV D-pad card/header navigation, deliberately at
        /// the DISPATCH stage — before the view tree sees the key. It cannot
        /// live in <see cref="OnKeyDown"/>: the card strips are
        /// HorizontalScrollViews whose own dispatchKeyEvent runs
        /// executeKeyEvent → arrowScroll for LEFT/RIGHT whenever the focused
        /// card declines the key, and below them
        /// ViewRootImpl.performFocusNavigation plays the system navigation
        /// click and instant-reveals targets. Owning the key here keeps both
        /// out of card navigation on every rail, regardless of how a card
        /// was materialized — the activity cannot be detached or recycled
        /// the way per-card platform views can. Non-direction keys (Back,
        /// DPAD_CENTER, media keys) and the open menu trap's per-item moves
        /// pass through untouched.
        /// </summary>
        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            // Own Back at dispatch so focused cards / Leanback cannot finish the
            // activity before OnKeyDown — open homepage menu must close first.
            if (e is { Action: KeyEventActions.Down, KeyCode: Keycode.Back, RepeatCount: 0 })
            {
                if (TryConsumeHomeOverlayBack())
                {
                    NoteOverlayBackConsumed();
                    return true;
                }

                if (_flyoutMenuOpen)
                {
                    SetFlyoutMenuOpen(false);
                    return true;
                }
            }

            if (e?.Action == KeyEventActions.Down
                && VardyParty.HomeUi.Views.TvDpadFocusRouter.TryHandleActivityKey(CurrentFocus, e.KeyCode))
            {
                return true;
            }

            return base.DispatchKeyEvent(e);
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
        {
            // Homepage menu / finding-streams must win before RemoteKeyHandler's
            // OnBack multicast (MainActivity.RemoteBackHandler can FinishAndRemoveTask).
            // Field: TV Back with the menu open exited the app when the key path
            // skipped OnBackPressed and the exit subscriber ran first.
            if (keyCode == Keycode.Back && TryConsumeHomeOverlayBack())
            {
                NoteOverlayBackConsumed();
                return true;
            }

            if (_remoteKeyHandler != null && _remoteKeyHandler.HandleKeyDown(keyCode, e))
            {
                return true;
            }

            // Menu-trap backstop: a direction key the open panel's items did
            // not consume must never reach the default focus search (it
            // could carry focus behind the scrim).
            if (VardyParty.HomeUi.Views.TvDpadFocusRouter.SealsMenuTrapKey(keyCode))
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
