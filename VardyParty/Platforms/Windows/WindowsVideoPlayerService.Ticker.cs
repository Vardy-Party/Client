using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using VardyParty.Catalog;
using VardyParty.Kernel;
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

            private List<Game> GetGamesSnapshot()
            {
                lock (gamesLock)
                {
                    return latestGamesByLeague == null
                        ? new List<Game>()
                        : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                }
            }

            private List<TickerDisplayPart> BuildCurrentModeTickerParts()
            {
                RefreshGamesSnapshot();
                var watched = ResolveWatchedContext();
                return ScoresTickerText.BuildParts(
                    scoresTickerMode,
                    GetGamesSnapshot(),
                    watched.League ?? watchedLeagueName,
                    watched.Home ?? watchedHomeTeam,
                    watched.Away ?? watchedAwayTeam);
            }

            private List<TickerDisplayPart> GetTickerEmptyParts(ScoresTickerMode mode)
            {
                var watched = ResolveWatchedContext();
                return ScoresTickerText.BuildParts(
                    mode,
                    Array.Empty<Game>(),
                    watched.League ?? watchedLeagueName,
                    watched.Home ?? watchedHomeTeam,
                    watched.Away ?? watchedAwayTeam);
            }

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

            private void SyncScoresTickerFromChrome()
            {
                try
                {
                    var wantVisible = chrome.IsScoresVisible;
                    var mode = chrome.ScoresMode;
                    var visibilityChanged = wantVisible != isScoresTickerVisible;
                    var modeChanged = mode != scoresTickerMode;
                    scoresTickerMode = mode;

                    if (visibilityChanged)
                    {
                        isScoresTickerVisible = wantVisible;
                        scoresTickerBorder.Visibility = wantVisible
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;

                        if (wantVisible)
                        {
                            RefreshGamesSnapshot();
                            RefreshTickerText(resetOffset: true);
                        }
                        else
                        {
                            StopTickerScroll();
                        }
                    }
                    else if (wantVisible && modeChanged)
                    {
                        RefreshTickerText(resetOffset: true);
                    }
                }
                catch (Exception ex)
                {
                    _host._logger.LogWarning(ex, "SyncScoresTickerFromChrome failed");
                    isScoresTickerVisible = false;
                    scoresTickerBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    scoresTickerScrollTimer?.Stop();
                }
            }

            private void CycleScoresTickerMode()
            {
                chrome.CycleScoresMode();
            }
        }
    }
}