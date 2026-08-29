#if ANDROID
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Extensions.Logging;
using VardyParty.HomeUi;
using VardyParty.Presentation;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// In-playback match-event banner: goals / extra time / penalties shown
    /// OVER the video, consuming the same delivered-event bus as the homepage
    /// toast (visibility-filtered, foreground-gated, "Goal notifications"
    /// toggle-gated — all upstream). Native views only: this activity has no
    /// MAUI tree. NO audio here by design ("playing → toast only").
    ///
    /// Artwork comes from the shared <see cref="IBadgeImageLoader"/> byte
    /// cache, so the banner renders the SAME league logo and team badges as
    /// the homepage toast from the same single fetch.
    ///
    /// Foreground ownership: covering the MAUI activity fires its window's
    /// Stopped event, but the app as a whole is still the visible surface —
    /// while this activity is up IT owns the policy flag (OnResume sets it;
    /// OnStop-while-not-finishing — HOME/another app over the player —
    /// clears it; finishing hands back to the MAUI window's Activated).
    /// </summary>
    public partial class NativeVideoActivity
    {
        private const int MatchToastDurationMs = 4000;
        private const int MatchToastMaxQueued = 3;

        private MatchEventBus? _matchEventBus;
        private MatchEventNotificationPolicy? _matchEventPolicy;
        private IBadgeImageLoader? _badgeLoader;
        private Action<MatchEvent>? _matchEventHandler;

        private LinearLayout? _matchToastView;
        private ImageView? _matchToastLeagueLogo;
        private ImageView? _matchToastHomeBadge;
        private ImageView? _matchToastAwayBadge;
        private TextView? _matchToastText;
        private global::Android.OS.Handler? _matchToastHandler;
        private Java.Lang.Runnable? _matchToastHideRunnable;
        private readonly Queue<MatchEvent> _matchToastQueue = new();
        private bool _matchToastShowing;
        private int _matchToastEpoch;

        private void InitializeMatchToast(FrameLayout root, float density)
        {
            try
            {
                var services = VardyParty.AppServiceProvider.ServiceProvider;
                _matchEventBus = services?.GetService(typeof(MatchEventBus)) as MatchEventBus;
                _matchEventPolicy = services?.GetService(typeof(MatchEventNotificationPolicy)) as MatchEventNotificationPolicy;
                _badgeLoader = services?.GetService(typeof(IBadgeImageLoader)) as IBadgeImageLoader;
                if (_matchEventBus == null) return;

                int Dp(float dp) => (int)(dp * density);

                _matchToastView = new LinearLayout(this)
                {
                    Orientation = Orientation.Horizontal,
                    Visibility = global::Android.Views.ViewStates.Gone,
                };
                _matchToastView.SetGravity(global::Android.Views.GravityFlags.CenterVertical);
                _matchToastView.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));

                var background = new global::Android.Graphics.Drawables.GradientDrawable();
                background.SetColor(global::Android.Graphics.Color.ParseColor("#E6101521"));
                background.SetCornerRadius(Dp(12));
                background.SetStroke(Dp(1), global::Android.Graphics.Color.ParseColor("#33FFFFFF"));
                _matchToastView.Background = background;

                ImageView MakeIcon(float sizeDp)
                {
                    var view = new ImageView(this);
                    var lp = new LinearLayout.LayoutParams(Dp(sizeDp), Dp(sizeDp)) { RightMargin = Dp(8) };
                    view.LayoutParameters = lp;
                    view.Visibility = global::Android.Views.ViewStates.Gone;
                    return view;
                }

                _matchToastLeagueLogo = MakeIcon(22);
                _matchToastHomeBadge = MakeIcon(26);

                _matchToastText = new TextView(this);
                _matchToastText.SetTextColor(global::Android.Graphics.Color.ParseColor("#F3F4F6"));
                _matchToastText.SetTextSize(global::Android.Util.ComplexUnitType.Dip, _isTvDevice ? 18 : 14);
                _matchToastText.SetTypeface(_matchToastText.Typeface, global::Android.Graphics.TypefaceStyle.Bold);

                _matchToastAwayBadge = MakeIcon(26);
                ((LinearLayout.LayoutParams)_matchToastAwayBadge.LayoutParameters!).LeftMargin = Dp(8);
                ((LinearLayout.LayoutParams)_matchToastAwayBadge.LayoutParameters!).RightMargin = 0;

                _matchToastView.AddView(_matchToastLeagueLogo);
                _matchToastView.AddView(_matchToastHomeBadge);
                _matchToastView.AddView(_matchToastText);
                _matchToastView.AddView(_matchToastAwayBadge);

                var toastParams = new FrameLayout.LayoutParams(
                    global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                    global::Android.Views.ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = global::Android.Views.GravityFlags.Top | global::Android.Views.GravityFlags.Right,
                    TopMargin = Dp(_isTvDevice ? 32 : 16),
                    RightMargin = Dp(_isTvDevice ? 48 : 16),
                };
                root.AddView(_matchToastView, toastParams);

                _matchToastHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
                _matchToastHideRunnable = new Java.Lang.Runnable(DismissMatchToast);

                // Bus callbacks arrive on the shared Android main looper.
                _matchEventHandler = OnMatchEventForBanner;
                _matchEventBus.Published += _matchEventHandler;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Match-event banner unavailable");
            }
        }

        private void OnMatchEventForBanner(MatchEvent matchEvent)
        {
            RunOnUiThread(() =>
            {
                if (_matchToastView == null) return;
                if (_matchToastQueue.Count >= MatchToastMaxQueued)
                {
                    _matchToastQueue.Dequeue(); // drop-oldest, same as the homepage toast
                }

                _matchToastQueue.Enqueue(matchEvent);
                if (!_matchToastShowing) ShowNextMatchToast();
            });
        }

        private void ShowNextMatchToast()
        {
            if (_matchToastView == null || _matchToastText == null || _matchToastQueue.Count == 0)
            {
                _matchToastShowing = false;
                return;
            }

            var matchEvent = _matchToastQueue.Dequeue();
            _matchToastShowing = true;
            var epoch = ++_matchToastEpoch;

            var game = matchEvent.Game;
            var headline = matchEvent.Kind switch
            {
                MatchEventKind.Goal => "GOAL",
                MatchEventKind.ExtraTime => "EXTRA TIME",
                MatchEventKind.Penalties => "PENALTIES",
                _ => "MATCH",
            };
            _matchToastText.Text =
                $"{headline} · {game.DisplayLeague}  —  {game.DisplayHome} {matchEvent.HomeScore}–{matchEvent.AwayScore} {game.DisplayAway}";

            // Artwork async from the shared byte cache; epoch-guarded so a
            // superseded banner never receives a late image.
            SetBadgeAsync(_matchToastLeagueLogo, ResolveLeagueLogoBytesAsync(game), epoch);
            SetBadgeAsync(_matchToastHomeBadge, _badgeLoader?.LoadRemoteBytesAsync(game.HomeBadgeUrl), epoch);
            SetBadgeAsync(_matchToastAwayBadge, _badgeLoader?.LoadRemoteBytesAsync(game.AwayBadgeUrl), epoch);

            _matchToastView.Alpha = 0f;
            _matchToastView.Visibility = global::Android.Views.ViewStates.Visible;
            _matchToastView.Animate()?.Alpha(1f)?.SetDuration(180)?.Start();

            if (_matchToastHandler != null && _matchToastHideRunnable != null)
            {
                _matchToastHandler.RemoveCallbacks(_matchToastHideRunnable);
                _matchToastHandler.PostDelayed(_matchToastHideRunnable, MatchToastDurationMs);
            }
        }

        private async Task<byte[]?> ResolveLeagueLogoBytesAsync(global::VardyParty.Kernel.Game game)
        {
            try
            {
                if (_badgeLoader == null) return null;
                var locator = VardyParty.AppServiceProvider.ServiceProvider?
                    .GetService(typeof(IHomeAssetLocator)) as IHomeAssetLocator;
                if (locator == null) return null;
                var path = await locator.ResolveLeagueLogoPathAsync(game).ConfigureAwait(false);
                return path == null ? null : await _badgeLoader.LoadLocalBytesAsync(path).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private void SetBadgeAsync(ImageView? target, Task<byte[]?>? bytesTask, int epoch)
        {
            if (target == null) return;
            target.Visibility = global::Android.Views.ViewStates.Gone;
            if (bytesTask == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await bytesTask.ConfigureAwait(false);
                    if (bytes == null) return;
                    var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    if (bitmap == null) return;

                    RunOnUiThread(() =>
                    {
                        if (epoch != _matchToastEpoch) return; // superseded banner
                        target.SetImageBitmap(bitmap);
                        target.Visibility = global::Android.Views.ViewStates.Visible;
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[NativeVideoActivity] Banner artwork load failed");
                }
            });
        }

        private void DismissMatchToast()
        {
            if (_matchToastView == null) return;
            _matchToastView.Animate()?.Alpha(0f)?.SetDuration(150)?.WithEndAction(new Java.Lang.Runnable(() =>
            {
                if (_matchToastView != null)
                {
                    _matchToastView.Visibility = global::Android.Views.ViewStates.Gone;
                }

                ShowNextMatchToast();
            }))?.Start();
        }

        /// <summary>While the player is up, IT owns the policy's foreground flag.</summary>
        protected override void OnResume()
        {
            base.OnResume();
            HideSystemUI();
            if (_matchEventPolicy != null) _matchEventPolicy.IsAppForegrounded = true;
        }

        private void MatchToastOnStop()
        {
            // HOME / another app covering the PLAYER = genuinely backgrounded.
            // Finishing (Back to the homepage) hands the flag to the MAUI
            // window's own Activated event.
            if (!IsFinishing && _matchEventPolicy != null)
            {
                _matchEventPolicy.IsAppForegrounded = false;
            }
        }

        private void MatchToastOnDestroy()
        {
            try
            {
                if (_matchEventBus != null && _matchEventHandler != null)
                {
                    _matchEventBus.Published -= _matchEventHandler;
                }

                if (_matchToastHandler != null && _matchToastHideRunnable != null)
                {
                    _matchToastHandler.RemoveCallbacks(_matchToastHideRunnable);
                }

                _matchToastQueue.Clear();
            }
            catch
            {
                // Banner teardown must never break player teardown.
            }
        }
    }
}
#endif
