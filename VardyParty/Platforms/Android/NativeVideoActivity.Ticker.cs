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
                        RunOnUiThread(UpdateScoresTickerText);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Unable to subscribe to enriched games stream");
            }
        }

        private List<string> BuildSameLeagueInPlayScoreLines()
        {
            Dictionary<string, List<Game>>? snapshot;
            lock (_gamesLock)
            {
                snapshot = _latestGamesByLeague == null
                    ? null
                    : _latestGamesByLeague.ToDictionary(k => k.Key, v => v.Value.ToList());
            }

            if (snapshot == null || snapshot.Count == 0)
            {
                return new List<string>();
            }

            bool SameTeam(string left, string right) =>
                string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

            bool IsCurrentGame(Game game)
            {
                var home = game.DisplayHome;
                var away = game.DisplayAway;
                return (SameTeam(home, _currentHomeTeam) && SameTeam(away, _currentAwayTeam))
                    || (SameTeam(home, _currentAwayTeam) && SameTeam(away, _currentHomeTeam));
            }

            bool IsSameLeague(Game game) => ScoresTickerPolicy.IsSameLeague(game, _currentLeague);

            return snapshot.Values
                .SelectMany(g => g)
                .Where(IsSameLeague)
                .Where(ScoresTickerPolicy.IsInPlay)
                .Where(g => !IsCurrentGame(g))
                .OrderByDescending(g => g.LiveMinuteForOrdering)
                .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                .Select(FormatTickerLine)
                .ToList();
        }

        private List<string> BuildAllLeaguesInPlayScoreLines()
        {
            var games = GetGamesSnapshot();
            if (games.Count == 0)
            {
                return new List<string>();
            }

            return games
                .Where(ScoresTickerPolicy.IsInPlay)
                .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(g => g.LiveMinuteForOrdering)
                .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"[{g.DisplayLeague}] {FormatTickerLine(g)}")
                .ToList();
        }

        private List<string> BuildFinishedScoreLines()
        {
            var games = GetGamesSnapshot();
            if (games.Count == 0)
            {
                return new List<string>();
            }

            return games
                .Where(g => ScoresTickerPolicy.IsFinishedWithScore(g))
                .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(g => g.StartUtcForOrdering)
                .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"[{g.DisplayLeague}] {FormatTickerLine(g)}")
                .ToList();
        }

        private List<string> BuildUpcomingScoreLines()
        {
            var games = GetGamesSnapshot();
            if (games.Count == 0)
            {
                return new List<string>();
            }

            return games
                .Where(ScoresTickerPolicy.IsUpcoming)
                .OrderBy(g => g.StartUtcForOrdering)
                .ThenBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"[{g.DisplayLeague}] {FormatUpcomingLine(g)}")
                .ToList();
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

        private static string FormatScore(Game game)
        {
            var homeScore = game.HomeScore?.ToString() ?? "-";
            var awayScore = game.AwayScore?.ToString() ?? "-";

            var score = $"{homeScore}-{awayScore}";
            if (game.AggregateHomeScore.HasValue || game.AggregateAwayScore.HasValue)
            {
                var aggregateHome = game.AggregateHomeScore?.ToString() ?? "-";
                var aggregateAway = game.AggregateAwayScore?.ToString() ?? "-";
                score += $" agg {aggregateHome}-{aggregateAway}";
            }

            return score;
        }

        private static string FormatTickerLine(Game game)
        {
            var status = game.DisplayStatusText();
            if (string.IsNullOrWhiteSpace(status))
            {
                status = game.IsFinished ? "FT" : "Live";
            }

            var international = InternationalTeamDisplay.IsInternationalGame(game);
            var home = FormatTeamForDisplay(game.DisplayHome, international);
            var away = FormatTeamForDisplay(game.DisplayAway, international);
            return $"{home} {FormatScore(game)} {away} ({status})";
        }

        private static string FormatUpcomingLine(Game game)
        {
            // Same rule as MatchStatusPresenter.FormatStartTime: exactly one
            // conversion to device-local; non-Local kinds are UTC by ingestion.
            var localKickoff = game.Start.Kind == DateTimeKind.Local ? game.Start : game.Start.ToLocalTime();
            var kickoffText = localKickoff == default ? "TBD" : localKickoff.ToString("HH:mm");
            var international = InternationalTeamDisplay.IsInternationalGame(game);
            var home = FormatTeamForDisplay(game.DisplayHome, international);
            var away = FormatTeamForDisplay(game.DisplayAway, international);
            return $"{kickoffText} {home} vs {away}";
        }

        private static string FormatTeamForDisplay(string? teamName, bool international)
        {
            return InternationalTeamDisplay.FormatTeamName(teamName, international);
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
            _scoresTickerMode = ScoresTickerPolicy.Next(_scoresTickerMode);

            if (_isScoresTickerVisible)
            {
                UpdateScoresTickerText();
            }
        }

        private void UpdateScoresTickerText()
        {
            try
            {
                string title;
                List<string> lines;

                switch (_scoresTickerMode)
                {
                    case ScoresTickerMode.AllLeaguesInPlay:
                        title = "All leagues in-play";
                        lines = BuildAllLeaguesInPlayScoreLines();
                        break;
                    case ScoresTickerMode.AllFinished:
                        title = "Finished games";
                        lines = BuildFinishedScoreLines();
                        break;
                    case ScoresTickerMode.AllUpcoming:
                        title = "Upcoming games";
                        lines = BuildUpcomingScoreLines();
                        break;
                    default:
                        title = string.IsNullOrWhiteSpace(_currentLeague) ? "In-play games" : $"In-play: {_currentLeague}";
                        lines = BuildSameLeagueInPlayScoreLines();
                        break;
                }

                var emptyMessage = _scoresTickerMode switch
                {
                    ScoresTickerMode.AllLeaguesInPlay => "No in-play games right now.",
                    ScoresTickerMode.AllFinished => "No finished games right now.",
                    ScoresTickerMode.AllUpcoming => "No remaining unstarted games today.",
                    _ => "No other in-play games in this league right now."
                };

                var message = lines.Count == 0 ? emptyMessage : string.Join(InternationalTeamDisplay.TickerSeparator, lines);
                var fullText = $"{title}: {message}";
                if (_tickerText1 != null) _tickerText1.Text = fullText;
                if (_tickerText2 != null) _tickerText2.Text = fullText;
                _tickerScrollX = 0f;
                if (_tickerInner != null) _tickerInner.TranslationX = 0f;
                RemoveCallback(_tickerHandler, _tickerRunnable);
                if (_isScoresTickerVisible)
                    PostDelayedCallback(_tickerHandler, _tickerRunnable, 16);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to update same-league ticker text");
            }
        }

        private void ToggleSameLeagueScoresTicker()
        {
            try
            {
                _isScoresTickerVisible = !_isScoresTickerVisible;
                if (_scoresTickerContainer != null)
                {
                    _scoresTickerContainer.Visibility = _isScoresTickerVisible
                        ? global::Android.Views.ViewStates.Visible
                        : global::Android.Views.ViewStates.Gone;
                }

                if (_isScoresTickerVisible)
                {
                    _scoresTickerMode = ScoresTickerMode.SameLeagueInPlay;
                    UpdateScoresTickerText();
                }
                else
                {
                    RemoveCallback(_tickerHandler, _tickerRunnable);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to toggle same-league ticker");
            }
        }
    }
}
#endif
