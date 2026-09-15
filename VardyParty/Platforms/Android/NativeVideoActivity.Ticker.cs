#if ANDROID
using System;
using System.Collections.Generic;
using System.Linq;
using Android.Widget;
using Microsoft.Extensions.Logging;
using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Platforms.Android
{
    public partial class NativeVideoActivity
    {
        private void SubscribeToGamesSnapshot()
        {
            try
            {
                if (_enrichedGames == null) return;

                _gamesSub = _enrichedGames.GamesStream.Subscribe(dict =>
                {
                    if (dict == null) return;
                    lock (_gamesLock)
                    {
                        _latestGamesByLeague = dict.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                    }

                    if (_isScoresTickerVisible)
                    {
                        RunOnUiThread(() => UpdateScoresTickerText());
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Unable to subscribe to enriched games stream");
            }
        }

        private List<Game> GetGamesSnapshot()
        {
            lock (_gamesLock)
            {
                return _latestGamesByLeague == null
                    ? new List<Game>()
                    : _latestGamesByLeague.Values.SelectMany(v => v).ToList();
            }
        }

        private static void ConfigureEmojiFriendlyTextView(TextView? textView)
        {
            if (textView == null) return;

            try
            {
                var typeface = global::Android.Graphics.Typeface.Create("sans-serif", global::Android.Graphics.TypefaceStyle.Normal);
                if (typeface != null)
                {
                    textView.SetTypeface(typeface, global::Android.Graphics.TypefaceStyle.Normal);
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Debug("VardyParty", $"[NativeVideoActivity] ConfigureEmojiFriendlyTextView failed: {ex.Message}");
            }
        }

        private void CycleScoresTickerMode()
        {
            EnsureChrome().CycleScoresMode();
        }

        private void UpdateScoresTickerText(bool armStartHold = false)
        {
            try
            {
                var snapshot = ScoresTickerText.Build(
                    _scoresTickerMode,
                    GetGamesSnapshot(),
                    _currentLeague,
                    _currentHomeTeam,
                    _currentAwayTeam);
                var fullText = snapshot.FullText;
                if (_tickerText1 != null) _tickerText1.Text = fullText;
                if (_tickerText2 != null) _tickerText2.Text = fullText;
                _tickerScrollX = 0f;
                _tickerCopyWidth = 0;
                ApplyTickerCopyLayout();
                if (_tickerTrack != null) _tickerTrack.TranslationX = 0f;
                if (armStartHold)
                {
                    _tickerScrollHoldUntilMs = global::Android.OS.SystemClock.UptimeMillis() + TickerScrollStartDelayMs;
                }

                RemoveCallback(_tickerHandler, _tickerRunnable);
                if (_isScoresTickerVisible)
                    PostDelayedCallback(_tickerHandler, _tickerRunnable, 16);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to update same-league ticker text");
            }
        }

        private void SyncScoresTickerFromChrome()
        {
            if (_chrome is null) return;

            try
            {
                var wantVisible = _chrome.IsScoresVisible;
                var mode = _chrome.ScoresMode;
                var visibilityChanged = wantVisible != _isScoresTickerVisible;
                var modeChanged = mode != _scoresTickerMode;
                _scoresTickerMode = mode;

                if (visibilityChanged)
                {
                    _isScoresTickerVisible = wantVisible;
                    if (_scoresTickerContainer != null)
                    {
                        _scoresTickerContainer.Visibility = wantVisible
                            ? global::Android.Views.ViewStates.Visible
                            : global::Android.Views.ViewStates.Gone;
                    }

                    if (wantVisible)
                        UpdateScoresTickerText(armStartHold: true);
                    else
                        RemoveCallback(_tickerHandler, _tickerRunnable);
                }
                else if (wantVisible && modeChanged)
                {
                    UpdateScoresTickerText(armStartHold: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to sync scores ticker from chrome");
            }
        }
    }
}
#endif
