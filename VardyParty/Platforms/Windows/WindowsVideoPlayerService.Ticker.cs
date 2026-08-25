using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using VardyParty.Extensions;
using VardyParty.Models;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private sealed partial class PlayerSession
        {
            private static string? StripTickerFlags(string? value) =>
                string.IsNullOrWhiteSpace(value)
                    ? null
                    : TickerMeasurePlainTextRegex.Replace(value, string.Empty).Trim();

            private (string? Home, string? Away, string? League) ResolveWatchedContext()
            {
                var resolvedHome = StripTickerFlags(_homeTeam);
                var resolvedAway = StripTickerFlags(_awayTeam);
                var resolvedLeague = string.IsNullOrWhiteSpace(_league) ? null : _league.Trim();

                if (!string.IsNullOrEmpty(resolvedHome) && !string.IsNullOrEmpty(resolvedAway))
                {
                    return (resolvedHome, resolvedAway, resolvedLeague);
                }

                if (!string.IsNullOrWhiteSpace(_title))
                {
                    var idx = _title.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        resolvedHome = StripTickerFlags(_title[..idx]);
                        resolvedAway = StripTickerFlags(_title[(idx + 4)..]);
                    }
                }

                return (resolvedHome, resolvedAway, resolvedLeague);
            }

            private void StopTickerScroll()
            {
                try { scoresTickerScrollTimer?.Stop(); } catch { }
            }

            private void LayoutScoresTicker()
            {
                var viewportWidth = scoresTickerViewport.ActualWidth;
                var viewportHeight = scoresTickerViewport.ActualHeight;
                if (viewportWidth <= 0 || viewportHeight <= 0) return;

                if (scoresTickerViewport.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rg)
                {
                    rg.Rect = new global::Windows.Foundation.Rect(0, 0, viewportWidth, viewportHeight);
                }

                scoresTickerTrack.VerticalAlignment = WinVerticalAlignment.Center;
                WindowsScoresTickerTrackBuilder.LayoutTrack(
                    scoresTickerTrack,
                    viewportWidth,
                    viewportHeight,
                    centerWhenFits: !scoresTickerLoopEnabled);
            }

            private void RebuildTickerTrackForViewport()
            {
                if (scoresTickerSingleCopy == null || scoresTickerSingleCopy.Count == 0)
                {
                    return;
                }

                var viewportHeight = Math.Max(scoresTickerViewport.ActualHeight, 24);
                var viewportWidth = scoresTickerViewport.ActualWidth;

                WindowsScoresTickerTrackBuilder.RebuildTrack(
                    scoresTickerTrack,
                    scoresTickerSingleCopy,
                    loopForScroll: false);
                WindowsScoresTickerTrackBuilder.MeasureTrack(
                    scoresTickerTrack,
                    viewportHeight,
                    out var singleCopyWidth);

                var needsLoop = TickerMarquee.ShouldLoop(singleCopyWidth, viewportWidth);
                if (needsLoop)
                {
                    WindowsScoresTickerTrackBuilder.RebuildTrack(
                        scoresTickerTrack,
                        scoresTickerSingleCopy,
                        loopForScroll: true);
                }

                if (scoresTickerLoopEnabled && !needsLoop)
                {
                    scoresTickerOffsetPx = 0;
                    tickerUserPaused = false;
                    tickerResumeCountdown = 0;
                    if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform resetTransform)
                    {
                        resetTransform.X = 0;
                    }
                }

                scoresTickerLoopEnabled = needsLoop;
                tickerMeasuredTextWidth = 0;
                tickerLoopWidth = 0;
            }

            private void SyncTickerScrollTimer()
            {
                if (!isScoresTickerVisible)
                {
                    return;
                }

                var viewportWidth = scoresTickerViewport.ActualWidth;
                if (!scoresTickerLoopEnabled || tickerLoopWidth <= 0)
                {
                    StopTickerScroll();
                    return;
                }

                EnsureTickerTimer();
                try { scoresTickerScrollTimer?.Start(); } catch { }
            }

            private void SyncTickerLayout()
            {
                if (!isScoresTickerVisible) return;
                RebuildTickerTrackForViewport();
                LayoutScoresTicker();
                UpdateTickerMeasurements();
                SyncTickerScrollTimer();
            }

            private void HandleTickerWheel(object? sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
            {
                var props = args.GetCurrentPoint(scoresTickerViewport).Properties;

                if (!props.IsHorizontalMouseWheel) return;

                var delta = props.MouseWheelDelta;
                if (delta == 0) return;

                if (!scoresTickerLoopEnabled || tickerLoopWidth <= 0) return;

                // -1 = actively interacting; countdown only starts on PointerExited
                tickerUserPaused = true;
                tickerResumeCountdown = -1;

                if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
                {
                    // Positive delta = swiped right = text should move right = offset increases
                    scoresTickerOffsetPx = TickerMarquee.Wrap(
                        scoresTickerOffsetPx - delta / 120.0 * 50.0,
                        tickerLoopWidth);
                    t.X = scoresTickerOffsetPx;
                }

                args.Handled = true;
            }

            private bool IsCurrentGame(Game g)
            {
                if (string.IsNullOrWhiteSpace(watchedHomeTeam) || string.IsNullOrWhiteSpace(watchedAwayTeam))
                {
                    return false;
                }

                var watchedKey = GameMatcher.BuildFixtureKey(watchedHomeTeam, watchedAwayTeam);
                var gameKey = GameMatcher.BuildFixtureKey(g.DisplayHome, g.DisplayAway);
                if (string.Equals(watchedKey, gameKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var swappedKey = GameMatcher.BuildFixtureKey(g.DisplayAway, g.DisplayHome);
                return string.Equals(watchedKey, swappedKey, StringComparison.OrdinalIgnoreCase);
            }

            private bool IsWatchedPreMatchFixture(Game g) =>
                IsCurrentGame(g) && !g.IsPostponed && !g.IsHalfTime && g.Minute is not > 0;

            private bool IsUpcomingForTicker(Game g, DateTime nowUtc)
            {
                if (g.IsPostponed || g.IsFinished)
                {
                    return false;
                }

                if (!BbcFixtureSchedule.IsWithinLookAheadWindow(g.StartUtcForOrdering, nowUtc))
                {
                    return false;
                }

                // Stream you're watching counts as upcoming until there is a real live minute.
                if (IsWatchedPreMatchFixture(g))
                {
                    return true;
                }

                var startUtc = g.StartUtcForOrdering;
                if (startUtc != default && startUtc != DateTime.MaxValue && startUtc > nowUtc.AddMinutes(5))
                {
                    return true;
                }

                return g.IsScheduledUpcoming(nowUtc);
            }

            private List<TickerDisplayPart> JoinSeparatedLineParts(string header, string emptyMessage, IReadOnlyList<List<TickerDisplayPart>> lines)
            {
                if (lines.Count == 0)
                {
                    return InternationalTeamDisplay.TextParts(emptyMessage).ToList();
                }

                var parts = new List<TickerDisplayPart>();
                parts.AddRange(InternationalTeamDisplay.TextParts(header));
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                    {
                        parts.AddRange(InternationalTeamDisplay.SeparatorParts());
                    }

                    parts.AddRange(lines[i]);
                }

                return parts;
            }

            private List<TickerDisplayPart> FormatUpcomingLineParts(Game g)
            {
                var local = g.Start.Kind == DateTimeKind.Utc ? g.Start.ToLocalTime() : g.Start;
                var ko = local == default ? "TBD" : local.ToString("HH:mm");
                var international = InternationalTeamDisplay.IsInternationalGame(g);
                var parts = new List<TickerDisplayPart>
                {
                    new($"[{g.DisplayLeague}]"),
                    new(ko),
                };
                parts.AddRange(InternationalTeamDisplay.TeamParts(g.DisplayHome, international));
                parts.Add(new("vs"));
                parts.AddRange(InternationalTeamDisplay.TeamParts(g.DisplayAway, international));
                return parts;
            }

            private List<TickerDisplayPart> FormatWatchedUpcomingFallbackParts(IReadOnlyList<Game> allGames)
            {
                if (string.IsNullOrWhiteSpace(watchedHomeTeam) || string.IsNullOrWhiteSpace(watchedAwayTeam))
                {
                    return new List<TickerDisplayPart>();
                }

                var watched = allGames.FirstOrDefault(IsCurrentGame);
                if (watched != null)
                {
                    return FormatUpcomingLineParts(watched);
                }

                var displayLeague = string.IsNullOrWhiteSpace(watchedLeagueName) ? "Match" : watchedLeagueName;
                var international = InternationalTeamDisplay.IsInternationalMatch(displayLeague, watchedHomeTeam, watchedAwayTeam);
                var parts = new List<TickerDisplayPart>
                {
                    new($"[{displayLeague}]"),
                    new("TBD"),
                };
                parts.AddRange(InternationalTeamDisplay.TeamParts(watchedHomeTeam, international));
                parts.Add(new("vs"));
                parts.AddRange(InternationalTeamDisplay.TeamParts(watchedAwayTeam, international));
                return parts;
            }

            private List<TickerDisplayPart> FormatInternationalTickerLineParts(Game g, string? statusOverride = null)
            {
                string FormatScoreLocal(Game game)
                {
                    var s = $"{game.HomeScore?.ToString() ?? "-"}-{game.AwayScore?.ToString() ?? "-"}";
                    if (game.AggregateHomeScore.HasValue || game.AggregateAwayScore.HasValue)
                        s += $" agg {game.AggregateHomeScore?.ToString() ?? "-"}-{game.AggregateAwayScore?.ToString() ?? "-"}";
                    return s;
                }

                var international = InternationalTeamDisplay.IsInternationalGame(g);
                var parts = new List<TickerDisplayPart>();
                parts.AddRange(InternationalTeamDisplay.TeamParts(g.DisplayHome, international));
                parts.Add(new($"  {FormatScoreLocal(g)}  "));
                parts.AddRange(InternationalTeamDisplay.TeamParts(g.DisplayAway, international));
                var status = statusOverride ?? g.DisplayStatusText();
                if (string.IsNullOrWhiteSpace(status)) status = "Live";
                parts.Add(new($"  ({status})"));
                return parts;
            }

            private List<TickerDisplayPart> BuildSameLeagueTickerParts()
            {
                Dictionary<string, List<Game>>? snapshot;
                lock (gamesLock)
                {
                    snapshot = latestGamesByLeague == null
                        ? null
                        : latestGamesByLeague.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                }

                if (snapshot == null || snapshot.Count == 0)
                {
                    return InternationalTeamDisplay.TextParts("In-play games: No same-league live scores available.").ToList();
                }

                bool IsSameLeague(Game g) => ScoresTickerPolicy.IsSameLeague(g, watchedLeagueName);

                var lines = snapshot.Values
                    .SelectMany(v => v)
                    .Where(IsSameLeague)
                    .Where(ScoresTickerPolicy.IsInPlay)
                    .Where(g => !IsCurrentGame(g))
                    .OrderByDescending(g => g.LiveMinuteForOrdering)
                    .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                    .Select((Game g) => FormatInternationalTickerLineParts(g))
                    .ToList();

                var header = string.IsNullOrWhiteSpace(watchedLeagueName) ? "In-play: " : $"In-play {watchedLeagueName}: ";
                return JoinSeparatedLineParts(
                    header,
                    $"{header.TrimEnd()} No other live games right now.",
                    lines);
            }

            private static string BuildAllLeaguesTickerDedupeKey(Game g)
            {
                var gameLeague = (g.DisplayLeague ?? string.Empty).Trim();
                return $"{gameLeague}|{GameMatcher.BuildFixtureKey(g.DisplayHome, g.DisplayAway)}";
            }

            private List<TickerDisplayPart> BuildAllLeaguesInPlayTickerParts()
            {
                List<Game> allGames;
                lock (gamesLock)
                {
                    allGames = latestGamesByLeague == null
                        ? new List<Game>()
                        : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                }

                var lines = allGames
                    .Where(ScoresTickerPolicy.IsInPlay)
                    .Where(g => !IsCurrentGame(g))
                    .DistinctBy(BuildAllLeaguesTickerDedupeKey)
                    .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(g => g.LiveMinuteForOrdering)
                    .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var line = new List<TickerDisplayPart> { new($"[{g.DisplayLeague}] ") };
                        line.AddRange(FormatInternationalTickerLineParts(g));
                        return line;
                    })
                    .ToList();

                return JoinSeparatedLineParts(
                    "All leagues in-play: ",
                    "All leagues in-play: No live games right now.",
                    lines);
            }

            private List<TickerDisplayPart> BuildFinishedScoresTickerParts()
            {
                List<Game> allGames;
                lock (gamesLock)
                {
                    allGames = latestGamesByLeague == null
                        ? new List<Game>()
                        : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                }

                var lines = allGames
                    .Where(ScoresTickerPolicy.IsFinishedWithScore)
                    .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(g => g.StartUtcForOrdering)
                    .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var line = new List<TickerDisplayPart> { new($"[{g.DisplayLeague}] ") };
                        line.AddRange(FormatInternationalTickerLineParts(g, "FT"));
                        return line;
                    })
                    .ToList();

                return JoinSeparatedLineParts(
                    "Finished games: ",
                    "Finished games: No finished games right now.",
                    lines);
            }

            private List<TickerDisplayPart> BuildUpcomingTickerParts()
            {
                RefreshGamesSnapshot();

                Dictionary<string, List<Game>>? snapshot;
                lock (gamesLock)
                {
                    snapshot = latestGamesByLeague == null
                        ? null
                        : latestGamesByLeague.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                }

                if (snapshot == null || snapshot.Count == 0)
                {
                    return InternationalTeamDisplay.TextParts("Upcoming games: Schedule not loaded yet.").ToList();
                }

                var allGames = snapshot.ToDisplay();
                var nowUtc = DateTime.UtcNow;
                var lines = allGames
                    .Where(g => IsUpcomingForTicker(g, nowUtc))
                    .OrderBy(g => g.StartUtcForOrdering)
                    .ThenBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                    .Select(FormatUpcomingLineParts)
                    .ToList();

                var watchedLine = FormatWatchedUpcomingFallbackParts(allGames);
                if (watchedLine.Count > 0)
                {
                    var watchedPlain = InternationalTeamDisplay.PartsToPlainText(watchedLine);
                    lines.RemoveAll(line => InternationalTeamDisplay.PartsToPlainText(line) == watchedPlain);
                    lines.Insert(0, watchedLine);
                }

                return JoinSeparatedLineParts(
                    "Upcoming games: ",
                    "Upcoming games: No unstarted games in the schedule window.",
                    lines);
            }

            private List<TickerDisplayPart> BuildCurrentModeTickerParts() => scoresTickerMode switch
            {
                ScoresTickerMode.AllLeaguesInPlay => BuildAllLeaguesInPlayTickerParts(),
                ScoresTickerMode.AllFinished => BuildFinishedScoresTickerParts(),
                ScoresTickerMode.AllUpcoming => BuildUpcomingTickerParts(),
                _ => BuildSameLeagueTickerParts()
            };

            private List<TickerDisplayPart> GetTickerEmptyParts(ScoresTickerMode mode) => mode switch
            {
                ScoresTickerMode.AllLeaguesInPlay => InternationalTeamDisplay.TextParts("All leagues in-play: No live games right now.").ToList(),
                ScoresTickerMode.AllFinished => InternationalTeamDisplay.TextParts("Finished games: No finished games right now.").ToList(),
                ScoresTickerMode.AllUpcoming => InternationalTeamDisplay.TextParts("Upcoming games: No unstarted games in the schedule window.").ToList(),
                _ => InternationalTeamDisplay.TextParts(
                    string.IsNullOrWhiteSpace(watchedLeagueName)
                        ? "In-play: No other live games right now."
                        : $"In-play {watchedLeagueName}: No other live games right now.").ToList()
            };

            private void EnsureTickerTimer()
            {
                scoresTickerScrollTimer ??= scoresTickerTrack.DispatcherQueue.CreateTimer();
                scoresTickerScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
                if (scoresTickerScrollHandler == null)
                {
                    scoresTickerScrollHandler = (_, __) =>
                    {
                        try
                        {
                            if (cleanupInvoked || !isScoresTickerVisible || scoresTickerSingleCopy == null || scoresTickerSingleCopy.Count == 0) return;

                            var viewportWidth = scoresTickerViewport.ActualWidth;
                            if (viewportWidth <= 0) return;

                            var transform = scoresTickerTrack.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
                            if (transform == null) return;

                            if (tickerMeasuredTextWidth <= 0 || tickerLoopWidth <= 0)
                            {
                                WindowsScoresTickerTrackBuilder.MeasureLoop(
                                    scoresTickerTrack,
                                    Math.Max(scoresTickerViewport.ActualHeight, 24),
                                    out var contentWidth,
                                    out var loopPeriod);
                                if (contentWidth <= 0) return;
                                tickerMeasuredTextWidth = contentWidth;
                                tickerLoopWidth = scoresTickerLoopEnabled ? loopPeriod : contentWidth;
                            }

                            // Only scroll when a single copy is wider than the viewport
                            if (!scoresTickerLoopEnabled)
                            {
                                if (!tickerUserPaused)
                                    transform.X = 0;
                                return;
                            }

                            // Handle resume countdown after user gesture / pointer-exit
                            if (tickerUserPaused)
                            {
                                if (tickerResumeCountdown > 0)
                                {
                                    tickerResumeCountdown--;
                                    if (tickerResumeCountdown == 0)
                                    {
                                        tickerUserPaused = false;
                                        tickerScrollDelayTicks = Math.Max(tickerScrollDelayTicks, TickerReadDelayTicks);
                                    }
                                }
                                return;
                            }

                            if (tickerScrollDelayTicks < TickerReadDelayTicks)
                            {
                                tickerScrollDelayTicks++;
                                transform.X = 0;
                                return;
                            }

                            scoresTickerOffsetPx = TickerMarquee.AdvanceLeft(
                                scoresTickerOffsetPx,
                                tickerSpeedPerTickPx,
                                tickerLoopWidth);
                            transform.X = scoresTickerOffsetPx;
                        }
                        catch (Exception ex)
                        {
                            _host._logger.LogWarning(ex, "Scores ticker tick failed");
                            scoresTickerScrollTimer?.Stop();
                        }
                    };
                    scoresTickerScrollTimer.Tick += scoresTickerScrollHandler;
                }
            }

            private void ApplyTickerParts(IReadOnlyList<TickerDisplayPart> singleCopy, bool resetOffset)
            {
                StopTickerScroll();

                scoresTickerSingleCopy = singleCopy.ToList();
                scoresTickerPlainPreview = InternationalTeamDisplay.PartsToPlainText(singleCopy);
                _host._logger.LogInformation(
                    "[ScoresTicker] mode={Mode} parts={PartCount} preview={Preview}",
                    scoresTickerMode,
                    singleCopy.Count,
                    TruncateForLog(scoresTickerPlainPreview, 120));

                tickerMeasuredTextWidth = 0;
                tickerLoopWidth = 0;

                if (resetOffset)
                {
                    scoresTickerOffsetPx = 0;
                    tickerScrollDelayTicks = 0;
                    tickerUserPaused = false;
                    tickerResumeCountdown = 0;
                }

                if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform transform)
                {
                    transform.X = scoresTickerOffsetPx;
                }

                SyncTickerLayout();
            }

            private void RefreshTickerText(bool resetOffset)
            {
                List<TickerDisplayPart> parts;
                try
                {
                    parts = BuildCurrentModeTickerParts();
                }
                catch (Exception ex)
                {
                    _host._logger.LogWarning(ex, "BuildTickerText failed");
                    parts = GetTickerEmptyParts(scoresTickerMode);
                }

                try
                {
                    var queue = nativeWindow?.DispatcherQueue;
                    if (queue != null && !queue.HasThreadAccess)
                    {
                        queue.TryEnqueue(() => ApplyTickerParts(parts, resetOffset));
                    }
                    else
                    {
                        ApplyTickerParts(parts, resetOffset);
                    }
                }
                catch (Exception ex)
                {
                    _host._logger.LogWarning(ex, "ApplyTickerParts failed");
                    var fallback = GetTickerEmptyParts(scoresTickerMode);
                    ApplyTickerParts(fallback, resetOffset);
                }
            }

            private void UpdateTickerMeasurements()
            {
                try
                {
                    if (scoresTickerViewport.ActualWidth <= 0 || scoresTickerSingleCopy == null || scoresTickerSingleCopy.Count == 0) return;

                    WindowsScoresTickerTrackBuilder.MeasureLoop(
                        scoresTickerTrack,
                        Math.Max(scoresTickerViewport.ActualHeight, 24),
                        out var contentWidth,
                        out var loopPeriod);
                    if (contentWidth <= 0) return;

                    tickerMeasuredTextWidth = contentWidth;
                    tickerLoopWidth = scoresTickerLoopEnabled ? loopPeriod : contentWidth;
                }
                catch (Exception ex)
                {
                    _host._logger.LogWarning(ex, "UpdateTickerMeasurements failed");
                }
            }

            private void RefreshGamesSnapshot(IEnrichedGameService? enrichedService = null)
            {
                var service = enrichedService ?? _host._enrichedGames;
                var dict = service?.GetLatestGames();
                if (dict == null) return;

                lock (gamesLock)
                {
                    latestGamesByLeague = dict.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                }
            }

            private void ToggleScoresTicker()
            {
                try
                {
                    isScoresTickerVisible = !isScoresTickerVisible;
                    scoresTickerBorder.Visibility = isScoresTickerVisible
                        ? Microsoft.UI.Xaml.Visibility.Visible
                        : Microsoft.UI.Xaml.Visibility.Collapsed;

                    if (isScoresTickerVisible)
                    {
                        RefreshGamesSnapshot();
                        scoresTickerMode = ScoresTickerMode.SameLeagueInPlay;
                        RefreshTickerText(resetOffset: true);
                    }
                    else
                    {
                        StopTickerScroll();
                    }
                }
                catch (Exception ex)
                {
                    _host._logger.LogWarning(ex, "ToggleScoresTicker failed");
                    isScoresTickerVisible = false;
                    scoresTickerBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    scoresTickerScrollTimer?.Stop();
                }
            }

            private void CycleScoresTickerMode()
            {
                scoresTickerMode = ScoresTickerPolicy.Next(scoresTickerMode);

                if (isScoresTickerVisible)
                {
                    RefreshTickerText(resetOffset: true);
                }
            }
        }
    }
}