using Microsoft.UI.Xaml.Controls;
using VardyParty.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Foundation;
using HttpClientWin = Windows.Web.Http.HttpClient;
using MauiApp = Microsoft.Maui.Controls.Application;
using WinButton = Microsoft.UI.Xaml.Controls.Button;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinThickness = Microsoft.UI.Xaml.Thickness;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using VardyParty.Health;
using VardyParty.Models;

namespace VardyParty.Platforms.Windows
{
    public class WindowsVideoPlayerService : INativeVideoPlayerService
    {
        public event EventHandler<bool>? BufferingStateChanged;

        private PlaybackMetrics? _currentMetrics;
        private MediaPlaybackItem? _currentPlaybackItem;

        public PlaybackMetrics? GetCurrentMetrics()
        {
            // Refresh bitrate from adaptive source before returning
            try
            {
                if (_currentPlaybackItem != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] GetCurrentMetrics: Refreshing bitrate from adaptive source...");
                    UpdateBitrateFromAdaptiveSource(_currentPlaybackItem);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] GetCurrentMetrics: No current playback item available");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Windows] GetCurrentMetrics: Failed to update bitrate: {ex.Message}");
            }
            
            System.Diagnostics.Debug.WriteLine($"[Windows] GetCurrentMetrics returning: Resolution={_currentMetrics?.Resolution}, Bitrate={_currentMetrics?.BitrateKbps}, Framerate={_currentMetrics?.Framerate}");
            return _currentMetrics;
        }

        public Task<PlaybackResult> PlayVideoAsync(
            string m3u8Url,
            string refererUrl,
            string title,
            Func<Task>? onNextStreamRequested = null,
            string? league = null,
            string? homeTeam = null,
            string? awayTeam = null)
        {
            var tcs = new TaskCompletionSource<PlaybackResult>();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Try to get the window from the Current application windows
                var mauiWindow = MauiApp.Current?.Windows.FirstOrDefault() ?? MauiApp.Current?.Windows.FirstOrDefault();
                var nativeWindow = mauiWindow?.Handler?.PlatformView as MauiWinUIWindow;

                if (nativeWindow == null)
                {
                    tcs.TrySetResult(PlaybackResult.Completed("No window available for playback.", true));
                    return;
                }

                var originalContent = nativeWindow.Content;

                var mediaPlayerElement = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true,
                    AutoPlay = true,
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Stretch
                };

                var mediaPlayer = new MediaPlayer();
                mediaPlayerElement.SetMediaPlayer(mediaPlayer);
                var cleanupInvoked = false;
                var currentPlaybackUrl = m3u8Url;
                var switchingService = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                var streamResolutionOrchestrator = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Orchestrators.IStreamResolutionOrchestrator)) as VardyParty.Orchestrators.IStreamResolutionOrchestrator;
                IDisposable? healthyStreamsSubscription = null;
                IDisposable? currentIndexSubscription = null;
                IDisposable? gamesSubscription = null;
                Microsoft.UI.Dispatching.DispatcherQueueTimer? streamInfoHideTimer = null;
                Microsoft.UI.Dispatching.DispatcherQueueTimer? scoresTickerScrollTimer = null as Microsoft.UI.Dispatching.DispatcherQueueTimer;
                TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? streamInfoHideHandler = null;
                TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? scoresTickerScrollHandler = null;
                int lastStreamTotal = -1;
                int lastStreamIndex = -1;
                bool isScoresTickerVisible = false;
                var scoresTickerRawText = string.Empty;
                double scoresTickerOffsetPx = 0;
                double tickerMeasuredTextWidth = 0;
                int tickerScrollDelayTicks = 0;
                const int TickerReadDelayTicks = 180; // ~3 seconds at 60fps before scrolling starts
                const double tickerSpeedPerTickPx = 1.5;
                Dictionary<string, List<Game>>? latestGamesByLeague = null;
                var gamesLock = new object();
                var scoresTickerMode = WindowsScoresTickerMode.SameLeagueInPlay;

                TypedEventHandler<MediaPlaybackSession, object>? playbackStateChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? naturalVideoSizeChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? positionChangedHandler = null;
                TypedEventHandler<MediaPlayer, object>? mediaEndedHandler = null;
                TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailedHandler = null;
                
                bool metadataReported = false;
                IStreamHealthReporter? _healthReporter = null;
                
                // Resolve health reporter
                try
                {
                    _healthReporter = ((((VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamHealthReporter)) as IStreamHealthReporter))));
                }
                catch { }

                void CleanupMediaPlayer()
                {
                    if (cleanupInvoked) return;
                    cleanupInvoked = true;

                    try
                    {
                        try { healthyStreamsSubscription?.Dispose(); } catch { }
                        try { currentIndexSubscription?.Dispose(); } catch { }
                        try { gamesSubscription?.Dispose(); } catch { }
                        try { streamInfoHideTimer?.Stop(); } catch { }
                        try { scoresTickerScrollTimer?.Stop(); } catch { }
                        if (naturalVideoSizeChangedHandler != null)
                            mediaPlayer.PlaybackSession.NaturalVideoSizeChanged -= naturalVideoSizeChangedHandler;
                        if (playbackStateChangedHandler != null)
                            mediaPlayer.PlaybackSession.PlaybackStateChanged -= playbackStateChangedHandler;
                        if (positionChangedHandler != null)
                            mediaPlayer.PlaybackSession.PositionChanged -= positionChangedHandler;
                        if (mediaEndedHandler != null)
                            mediaPlayer.MediaEnded -= mediaEndedHandler;
                        if (mediaFailedHandler != null)
                            mediaPlayer.MediaFailed -= mediaFailedHandler;
                    }
                    catch { }

                    try
                    {
                        mediaPlayer.Pause();
                        mediaPlayer.Source = null;
                        _currentPlaybackItem = null;
                    }
                    catch { }

                    try
                    {
                        mediaPlayerElement.SetMediaPlayer(null);
                    }
                    catch { }

                    try
                    {
                        mediaPlayer.Dispose();
                    }
                    catch { }
                }
                // Top-left hamburger button — sits in the top-left corner just below the title bar
                var menuButton = new WinButton
                {
                    Content = "☰",
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(14, 48, 0, 0),
                    Width = 42,
                    Height = 42,
                    Opacity = 0,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4)
                };

                // Menu panel opens downward from the button, indented to match button left edge
                var menuPanel = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(14, 98, 0, 0),
                    Spacing = 4,
                    Padding = new WinThickness(8),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xEE, 0x1A, 0x1A, 0x1A)),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6)
                };

                var videoInfoButton = new WinButton { Content = "Video Info" };
                var sameLeagueTickerButton = new WinButton { Content = "Scores" };
                var alwaysOnTopButton = new WinButton { Content = "📌 Always on top: Off" };
                var reportStreamButton = new WinButton { Content = "Report stream" };
                var reportStatusText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = "Reporting stream...",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    FontSize = 12
                };

                menuPanel.Children.Add(videoInfoButton);
                menuPanel.Children.Add(sameLeagueTickerButton);
                menuPanel.Children.Add(alwaysOnTopButton);
                menuPanel.Children.Add(reportStreamButton);
                menuPanel.Children.Add(reportStatusText);

                var scoresTickerBorder = new Microsoft.UI.Xaml.Controls.Border
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Bottom,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.80 },
                    Padding = new WinThickness(0, 6, 0, 6),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    IsHitTestVisible = true
                };
                var scoresTickerGrid = new WinGrid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Center
                };
                scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
                var scoresTickerViewport = new Microsoft.UI.Xaml.Controls.Grid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry()
                };
                WinGrid.SetColumn(scoresTickerViewport, 0);
                var scoresTickerText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 15,
                    TextTrimming = Microsoft.UI.Xaml.TextTrimming.None,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    // MaxWidth must be unconstrained so Measure() returns the true text width
                    MaxWidth = double.PositiveInfinity,
                    RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform()
                };
                var tickerCycleButton = new WinButton
                {
                    Content = "⟳",
                    FontSize = 15,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new WinThickness(0),
                    Padding = new WinThickness(8, 0, 8, 0),
                    VerticalAlignment = WinVerticalAlignment.Center
                };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tickerCycleButton, "Switch scores view");
                WinGrid.SetColumn(tickerCycleButton, 1);
                scoresTickerViewport.SizeChanged += (_, __) =>
                {
                    if (scoresTickerViewport.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rg)
                    {
                        rg.Rect = new global::Windows.Foundation.Rect(0, 0, scoresTickerViewport.ActualWidth, scoresTickerViewport.ActualHeight);
                    }
                };
                scoresTickerViewport.Children.Add(scoresTickerText);
                scoresTickerGrid.Children.Add(scoresTickerViewport);
                scoresTickerGrid.Children.Add(tickerCycleButton);
                scoresTickerBorder.Child = scoresTickerGrid;

                // Full-screen click-away surface for menu/info dismiss
                var dismissSurface = new Microsoft.UI.Xaml.Controls.Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Stretch,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
                };

                // Info Overlay — positioned to the right of the menu button to avoid overlap
                var infoPanel = new Microsoft.UI.Xaml.Controls.Grid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(70, 48, 0, 0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.7 },
                    Padding = new WinThickness(10),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    BorderThickness = new WinThickness(1)
                };

                var infoText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12
                };
                infoPanel.Children.Add(infoText);

                TypedEventHandler<Microsoft.UI.Windowing.AppWindow, Microsoft.UI.Windowing.AppWindowClosingEventArgs>? appWindowClosingHandler = null;

                void Restore()
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        CleanupMediaPlayer();
                        nativeWindow.Content = originalContent;
                    });
                }

                void UpdateInfo()
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (infoPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;

                        var session = mediaPlayer.PlaybackSession;
                        var width = session.NaturalVideoWidth;
                        var height = session.NaturalVideoHeight;
                        var state = session.PlaybackState.ToString();
                        
                        // Get additional info from MediaPlaybackItem if available
                        string frameRateText = "unknown";
                        string vCodec = "unknown";
                        string aCodec = "unknown";
                        AdaptiveMediaSource ams = null;

                        if (mediaPlayer.Source is MediaPlaybackItem item)
                        {
                            // Extract and store video metadata for health reporting
                            ExtractVideoMetadata(item, mediaPlayer);
                        }
                        else if (mediaPlayer.Source is MediaSource ms)
                        {
                            ams = ms.AdaptiveMediaSource;
                        }

                        var sb = new System.Text.StringBuilder();

                        int streamIndex = 0;
                        int streamTotal = 0;
                        string? streamChannel = null;
                        string? streamQuality = null;
                        try
                        {
                            var switching = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                            if (switching != null)
                            {
                                streamIndex = switching.GetCurrentStreamIndex();
                                streamTotal = switching.GetHealthyStreams().Count;
                                var current = switching.GetCurrentStream();
                                streamChannel = current?.Stream?.Channel;
                                try { streamQuality = current?.GetQualityDisplay(); } catch { }
                            }
                        }
                        catch { }

                        sb.AppendLine($"Status: {state}");
                        if (streamTotal > 0)
                            sb.AppendLine($"Stream: {streamIndex}/{streamTotal}");
                        if (!string.IsNullOrEmpty(streamChannel))
                            sb.AppendLine($"Channel: {streamChannel}");
                        if (!string.IsNullOrEmpty(streamQuality))
                            sb.AppendLine($"Quality: {streamQuality}");
                        sb.AppendLine($"Resolution: {width}x{height} @ {frameRateText}");

                        if (width > 0 && height > 0)
                        {
                            double r = (double)width / height;
                            int gcd(int a, int b) => b == 0 ? a : gcd(b, a % b);
                            int g = gcd((int)width, (int)height);
                            sb.AppendLine($"Aspect ratio: {(int)width / g}:{(int)height / g} ({r:0.00})");
                        }
                        else
                        {
                            sb.AppendLine($"Aspect ratio: pending");
                        }

                        string bitrateText = "unknown";
                        if (ams != null && ams.CurrentDownloadBitrate > 0)
                            bitrateText = $"{ams.CurrentDownloadBitrate / 1024.0:0.0} kbps";
                        
                        sb.AppendLine($"Bitrate: {bitrateText}");
                        sb.AppendLine($"Video Codec: {vCodec}");
                        sb.AppendLine($"Audio Codec: {aCodec}");
                        

                        if (!string.IsNullOrEmpty(title))
                            sb.AppendLine($"{title}");

                        double bufferingProgress = 0;
                        try
                        {
                            bufferingProgress = session.BufferingProgress;
                        }
                        catch { }
                        sb.AppendLine($"Buffer: {bufferingProgress * 100:0}%");

                        string StripQuery(string url)
                        {
                            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
                            try
                            {
                                var uri = new Uri(url);
                                var builder = new UriBuilder(uri) { Query = string.Empty };
                                return builder.Uri.ToString();
                            }
                            catch
                            {
                                var idx = url.IndexOf('?', StringComparison.Ordinal);
                                return idx >= 0 ? url.Substring(0, idx) : url;
                            }
                        }

                        string refHost = refererUrl;
                        if (Uri.TryCreate(refererUrl, UriKind.Absolute, out var rUri)) refHost = rUri.Host;

                        var source = StripQuery(currentPlaybackUrl);
                        if (!string.IsNullOrEmpty(source))
                            sb.AppendLine($"Source: {source}");
                        if (!string.IsNullOrEmpty(refHost))
                            sb.AppendLine($"Referer: {refHost}");

                        infoText.Text = sb.ToString();
                    });
                }

                string BuildSameLeagueTickerText()
                {
                    Dictionary<string, List<Game>>? snapshot;
                    lock (gamesLock)
                    {
                        snapshot = latestGamesByLeague == null
                            ? null
                            : latestGamesByLeague.ToDictionary(k => k.Key, v => v.Value.ToList());
                    }

                    if (snapshot == null || snapshot.Count == 0)
                    {
                        return "In-play games: No same-league live scores available.";
                    }

                    bool SameTeam(string left, string right) =>
                        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

                    bool IsCurrentGame(Game g)
                    {
                        var gh = g.DisplayHome;
                        var ga = g.DisplayAway;
                        return (SameTeam(gh, homeTeam ?? string.Empty) && SameTeam(ga, awayTeam ?? string.Empty))
                            || (SameTeam(gh, awayTeam ?? string.Empty) && SameTeam(ga, homeTeam ?? string.Empty));
                    }

                    bool IsSameLeague(Game g)
                    {
                        if (string.IsNullOrWhiteSpace(league)) return true;
                        return string.Equals((g.DisplayLeague ?? string.Empty).Trim(), league.Trim(), StringComparison.OrdinalIgnoreCase);
                    }

                    bool IsInPlay(Game g)
                    {
                        if (g.IsFinished || g.IsPostponed) return false;
                        return g.IsInProgress || g.IsHalfTime || g.Minute.HasValue;
                    }

                    string FormatScore(Game g)
                    {
                        var s = $"{g.HomeScore?.ToString() ?? "-"}-{g.AwayScore?.ToString() ?? "-"}";
                        if (g.AggregateHomeScore.HasValue || g.AggregateAwayScore.HasValue)
                            s += $" agg {g.AggregateHomeScore?.ToString() ?? "-"}-{g.AggregateAwayScore?.ToString() ?? "-"}";
                        return s;
                    }

                    string FormatStatus(Game g)
                    {
                        var st = g.DisplayStatusText();
                        return string.IsNullOrWhiteSpace(st) ? "Live" : st;
                    }

                    var lines = snapshot.Values
                        .SelectMany(v => v)
                        .Where(IsSameLeague)
                        .Where(IsInPlay)
                        .Where(g => !IsCurrentGame(g))
                        .OrderByDescending(g => g.LiveMinuteForOrdering)
                        .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                        .Select(g => $"{g.DisplayHome} {FormatScore(g)} {g.DisplayAway} ({FormatStatus(g)})")
                        .ToList();

                    var header = string.IsNullOrWhiteSpace(league) ? "In-play" : $"In-play {league}";
                    return lines.Count == 0
                        ? $"{header}: No other live games right now."
                        : $"{header}: {string.Join("   •   ", lines)}";
                }

                string BuildAllLeaguesInPlayTickerText()
                {
                    List<Game> allGames;
                    lock (gamesLock)
                    {
                        allGames = latestGamesByLeague == null
                            ? new List<Game>()
                            : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                    }

                    string FormatScore(Game g)
                    {
                        var s = $"{g.HomeScore?.ToString() ?? "-"}-{g.AwayScore?.ToString() ?? "-"}";
                        if (g.AggregateHomeScore.HasValue || g.AggregateAwayScore.HasValue)
                            s += $" agg {g.AggregateHomeScore?.ToString() ?? "-"}-{g.AggregateAwayScore?.ToString() ?? "-"}";
                        return s;
                    }

                    string FormatStatus(Game g)
                    {
                        var st = g.DisplayStatusText();
                        return string.IsNullOrWhiteSpace(st) ? "Live" : st;
                    }

                    var lines = allGames
                        .Where(g => !g.IsFinished && !g.IsPostponed && (g.IsInProgress || g.IsHalfTime || g.Minute.HasValue))
                        .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(g => g.LiveMinuteForOrdering)
                        .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                        .Select(g => $"[{g.DisplayLeague}] {g.DisplayHome} {FormatScore(g)} {g.DisplayAway} ({FormatStatus(g)})")
                        .ToList();

                    return lines.Count == 0
                        ? "All leagues in-play: No live games right now."
                        : $"All leagues in-play: {string.Join("   •   ", lines)}";
                }

                string BuildFinishedScoresTickerText()
                {
                    List<Game> allGames;
                    lock (gamesLock)
                    {
                        allGames = latestGamesByLeague == null
                            ? new List<Game>()
                            : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                    }

                    string FormatScore(Game g)
                    {
                        var s = $"{g.HomeScore?.ToString() ?? "-"}-{g.AwayScore?.ToString() ?? "-"}";
                        if (g.AggregateHomeScore.HasValue || g.AggregateAwayScore.HasValue)
                            s += $" agg {g.AggregateHomeScore?.ToString() ?? "-"}-{g.AggregateAwayScore?.ToString() ?? "-"}";
                        return s;
                    }

                    var lines = allGames
                        .Where(g => g.IsFinished)
                        .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(g => g.StartUtcForOrdering)
                        .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                        .Select(g => $"[{g.DisplayLeague}] {g.DisplayHome} {FormatScore(g)} {g.DisplayAway} (FT)")
                        .ToList();

                    return lines.Count == 0
                        ? "Finished games: No finished games right now."
                        : $"Finished games: {string.Join("   •   ", lines)}";
                }

                string BuildUpcomingTickerText()
                {
                    List<Game> allGames;
                    lock (gamesLock)
                    {
                        allGames = latestGamesByLeague == null
                            ? new List<Game>()
                            : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                    }

                    var lines = allGames
                        .Where(g => !g.IsFinished && !g.IsPostponed && !g.IsInProgress && !g.IsHalfTime && !g.Minute.HasValue)
                        .OrderBy(g => g.StartUtcForOrdering)
                        .ThenBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                        .Select(g =>
                        {
                            var local = g.Start.Kind == DateTimeKind.Utc ? g.Start.ToLocalTime() : g.Start;
                            var ko = local == default ? "TBD" : local.ToString("HH:mm");
                            return $"[{g.DisplayLeague}] {ko} {g.DisplayHome} vs {g.DisplayAway}";
                        })
                        .ToList();

                    return lines.Count == 0
                        ? "Upcoming games: No remaining unstarted games today."
                        : $"Upcoming games: {string.Join("   •   ", lines)}";
                }

                string BuildCurrentModeTickerText() => scoresTickerMode switch
                {
                    WindowsScoresTickerMode.AllLeaguesInPlay => BuildAllLeaguesInPlayTickerText(),
                    WindowsScoresTickerMode.AllFinished      => BuildFinishedScoresTickerText(),
                    WindowsScoresTickerMode.AllUpcoming      => BuildUpcomingTickerText(),
                    _                                        => BuildSameLeagueTickerText()
                };

                void EnsureTickerTimer()
                {
                    scoresTickerScrollTimer ??= scoresTickerText.DispatcherQueue.CreateTimer();
                    scoresTickerScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
                    if (scoresTickerScrollHandler == null)
                    {
                        scoresTickerScrollHandler = (_, __) =>
                        {
                            if (!isScoresTickerVisible || string.IsNullOrEmpty(scoresTickerRawText)) return;

                            var viewportWidth = scoresTickerViewport.ActualWidth;
                            if (viewportWidth <= 0) return;

                            var transform = scoresTickerText.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
                            if (transform == null) return;

                            // Measure the true text width (unconstrained) once per text update
                            if (tickerMeasuredTextWidth <= 0)
                            {
                                scoresTickerText.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                                tickerMeasuredTextWidth = scoresTickerText.DesiredSize.Width;
                                if (tickerMeasuredTextWidth <= 0) return;
                            }

                            // Only scroll if text is wider than the viewport
                            if (tickerMeasuredTextWidth <= viewportWidth)
                            {
                                transform.X = 0;
                                return;
                            }

                            // Wait for the initial read delay before starting to scroll
                            if (tickerScrollDelayTicks < TickerReadDelayTicks)
                            {
                                tickerScrollDelayTicks++;
                                transform.X = 0;
                                return;
                            }

                            scoresTickerOffsetPx -= tickerSpeedPerTickPx;

                            // When text has fully scrolled off the left, reset to start
                            // (text is always at X=0 initially so after a full scroll left it wraps back)
                            if (scoresTickerOffsetPx < -tickerMeasuredTextWidth)
                            {
                                scoresTickerOffsetPx = 0;
                                tickerScrollDelayTicks = 0; // re-apply read delay on each loop
                            }

                            transform.X = scoresTickerOffsetPx;
                        };
                        scoresTickerScrollTimer.Tick += scoresTickerScrollHandler;
                    }
                }

                void RefreshTickerText(bool resetOffset)
                {
                    scoresTickerRawText = BuildCurrentModeTickerText();
                    if (scoresTickerRawText.Length < 5)
                    {
                        scoresTickerRawText += "     ";
                    }

                    scoresTickerText.Text = scoresTickerRawText + "   ";

                    // Reset measured width so it is re-measured on the next tick with new text
                    tickerMeasuredTextWidth = 0;

                    if (resetOffset)
                    {
                        scoresTickerOffsetPx = 0;
                        tickerScrollDelayTicks = 0;
                    }

                    if (scoresTickerText.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform transform)
                    {
                        transform.X = scoresTickerOffsetPx;
                    }
                }

                void ToggleScoresTicker()
                {
                    isScoresTickerVisible = !isScoresTickerVisible;
                    scoresTickerBorder.Visibility = isScoresTickerVisible
                        ? Microsoft.UI.Xaml.Visibility.Visible
                        : Microsoft.UI.Xaml.Visibility.Collapsed;

                    if (isScoresTickerVisible)
                    {
                        scoresTickerMode = WindowsScoresTickerMode.SameLeagueInPlay;
                        EnsureTickerTimer();
                        RefreshTickerText(resetOffset: true);
                        scoresTickerScrollTimer?.Start();
                    }
                    else
                    {
                        scoresTickerScrollTimer?.Stop();
                    }
                }

                void CycleScoresTickerMode()
                {
                    scoresTickerMode = scoresTickerMode switch
                    {
                        WindowsScoresTickerMode.SameLeagueInPlay => WindowsScoresTickerMode.AllLeaguesInPlay,
                        WindowsScoresTickerMode.AllLeaguesInPlay => WindowsScoresTickerMode.AllFinished,
                        WindowsScoresTickerMode.AllFinished      => WindowsScoresTickerMode.AllUpcoming,
                        _                                        => WindowsScoresTickerMode.SameLeagueInPlay
                    };

                    if (isScoresTickerVisible)
                    {
                        RefreshTickerText(resetOffset: true);
                    }
                }

                tickerCycleButton.Click += (_, __) => CycleScoresTickerMode();

                void RefreshDismissSurface()
                {
                    dismissSurface.Visibility =
                        menuPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible ||
                        infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;
                }

                menuButton.Click += (_, __) =>
                {
                    menuPanel.Visibility = menuPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                        ? Microsoft.UI.Xaml.Visibility.Collapsed
                        : Microsoft.UI.Xaml.Visibility.Visible;
                    RefreshDismissSurface();
                };

                videoInfoButton.Click += (_, __) =>
                {
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    UpdateInfo();
                    RefreshDismissSurface();
                };

                sameLeagueTickerButton.Click += (_, __) =>
                {
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    ToggleScoresTicker();
                    RefreshDismissSurface();
                };

                alwaysOnTopButton.Click += (_, __) =>
                {
                    try
                    {
                        var appWindow = nativeWindow.AppWindow;
                        if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        {
                            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
                            alwaysOnTopButton.Content = presenter.IsAlwaysOnTop
                                ? "📌 Always on top: On"
                                : "📌 Always on top: Off";
                        }
                    }
                    catch { }
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    RefreshDismissSurface();
                };

                reportStreamButton.Click += async (_, __) =>
                {
                    reportStatusText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    reportStatusText.Text = "Reporting stream...";

                    try
                    {
                        if (streamResolutionOrchestrator != null)
                        {
                            await streamResolutionOrchestrator.ReportCurrentStreamAsBadAsync("User reported bad stream");
                            reportStatusText.Text = "Stream reported";
                        }
                        else
                        {
                            reportStatusText.Text = "Report unavailable";
                        }
                    }
                    catch
                    {
                        reportStatusText.Text = "Report failed";
                    }

                    await Task.Delay(900);
                    reportStatusText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    RefreshDismissSurface();
                };

                dismissSurface.PointerPressed += (_, __) =>
                {
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    RefreshDismissSurface();
                };

                infoPanel.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                };

                mediaEndedHandler = (s, e) => { Restore(); tcs.TrySetResult(PlaybackResult.Completed("Stream ended.", false)); };
                mediaFailedHandler = (s, e) =>
                {
                    try
                    {
                        var errMsg = e?.ErrorMessage ?? "Unknown media error";
                        var ext = string.Empty;
                        try { ext = e?.ExtendedErrorCode?.Message ?? string.Empty; } catch { }

                        // Ensure we cleanup health checking so Home can restart
                        try
                        {
                            var svc = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                            svc?.Cleanup();
                        }
                        catch { }

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                infoText.Text = $"Media failed: {errMsg}\n{ext}";
                            }
                            catch { }
                        });

                        // Restore UI and signal failure so Home moves to next stream
                        Restore();
                        tcs.TrySetResult(PlaybackResult.Completed($"Stream error on Windows player. Error: '{errMsg}'. Extended-message: '{ext}'.", true));
                    }
                    catch
                    {
                        try { Restore(); } catch { }
                        tcs.TrySetResult(PlaybackResult.Completed("Stream error on Windows player.", true));
                    }
                };

                // Detect buffering state from PlaybackSession
                playbackStateChangedHandler = (session, _) =>
                {
                    var isBuffering = session.PlaybackState == MediaPlaybackState.Buffering;
                    BufferingStateChanged?.Invoke(this, isBuffering);
                };

                mediaPlayer.PlaybackSession.PlaybackStateChanged += playbackStateChangedHandler;

                mediaPlayer.MediaEnded += mediaEndedHandler;
                mediaPlayer.MediaFailed += mediaFailedHandler;
                
                // Hook into playback session changes to update info if visible
                naturalVideoSizeChangedHandler = (s, e) => 
                {
                    // Extract metadata when format is decoded
                    if (mediaPlayer.Source is MediaPlaybackItem item)
                    {
                        ExtractVideoMetadata(item, mediaPlayer);
                        UpdateBitrateFromAdaptiveSource(item);  // Refresh bitrate
                    }
                    UpdateInfo();
                };
                
                positionChangedHandler = (s, e) => 
                {
                    try
                    {
                        // Periodically update bitrate during playback
                        if (mediaPlayer.Source is MediaPlaybackItem item)
                        {
                            UpdateBitrateFromAdaptiveSource(item);
                        }
                    }
                    catch (InvalidCastException ex)
                    {
                        // Silently ignore cast exceptions during position updates to avoid spam
                        // This can happen during adaptive stream switches
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] Position handler error: {ex.GetType().Name} - {ex.Message}");
                    }
                    
                    try
                    {
                        UpdateInfo();
                    }
                    catch { }
                };
                
                mediaPlayer.PlaybackSession.NaturalVideoSizeChanged += naturalVideoSizeChangedHandler;
                mediaPlayer.PlaybackSession.PositionChanged += positionChangedHandler;

                // Stream info display (bottom right)
                var streamInfoPanel = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Bottom,
                    Margin = new WinThickness(0, 0, 10, 10),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.7 },
                    Padding = new WinThickness(10),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    BorderThickness = new WinThickness(1),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
                };

                var streamCountText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Text = ""
                };
                streamInfoPanel.Children.Add(streamCountText);

                // Next stream button — right edge, vertically centred, icon only
                var nextButton = new WinButton
                {
                    Content = "⏭",
                    FontSize = 22,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)),
                    BorderThickness = new WinThickness(0),
                    Padding = new WinThickness(12, 14, 12, 14),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    Margin = new WinThickness(0, 0, 16, 0),
                    Opacity = 0,
                    Visibility = onNextStreamRequested != null
                        ? Microsoft.UI.Xaml.Visibility.Visible
                        : Microsoft.UI.Xaml.Visibility.Collapsed
                };

                var grid = new WinGrid();
                grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
                // Don't add mediaPlayerElement here yet - defer until source is initialized
                grid.Children.Add(dismissSurface);
                grid.Children.Add(infoPanel);
                grid.Children.Add(streamInfoPanel);
                grid.Children.Add(scoresTickerBorder);
                grid.Children.Add(menuPanel);
                grid.Children.Add(menuButton);
                grid.Children.Add(nextButton);

                nextButton.Click += async (s, e) =>
                {
                    if (onNextStreamRequested != null)
                    {
                        await onNextStreamRequested();
                        await TrySwitchToCurrentStreamAsync(force: true);
                    }
                };

                // Show/hide Next button on mouse hover, with background feedback
                if (onNextStreamRequested != null)
                {
                    var nextBgNormal = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
                    var nextBgHover  = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x30));
                    grid.PointerEntered += (s, e) => nextButton.Opacity = 1;
                    grid.PointerExited  += (s, e) => { nextButton.Opacity = 0; nextButton.Background = nextBgNormal; };
                    nextButton.PointerEntered += (s, e) => nextButton.Background = nextBgHover;
                    nextButton.PointerExited  += (s, e) => nextButton.Background = nextBgNormal;
                }

                grid.PointerMoved += (s, e) =>
                {
                    try
                    {
                        var p = e.GetCurrentPoint(grid).Position;
                        var inTopLeftQuadrant = p.X <= grid.ActualWidth / 2 && p.Y <= grid.ActualHeight / 2;

                        if (menuPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                        {
                            menuButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                            menuButton.Opacity = 1;
                            return;
                        }

                        menuButton.Visibility = inTopLeftQuadrant
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;
                        menuButton.Opacity = inTopLeftQuadrant ? 1 : 0;
                    }
                    catch { }
                };

                void UpdateStreamInfo()
                {
                    if (switchingService == null) return;
                    try
                    {
                        var total = switchingService.GetHealthyStreams().Count;
                        var index = switchingService.GetCurrentStreamIndex();
                        var hasChanged = total != lastStreamTotal || index != lastStreamIndex;
                        lastStreamTotal = total;
                        lastStreamIndex = index;
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (total > 0)
                            {
                                streamCountText.Text = $"Stream: {index}/{total}";
                            }
                            else
                            {
                                streamCountText.Text = "Streams: 0";
                            }

                            if (hasChanged)
                            {
                                streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                streamInfoHideTimer ??= streamInfoPanel.DispatcherQueue.CreateTimer();
                                streamInfoHideTimer.Interval = TimeSpan.FromSeconds(10);
                                streamInfoHideTimer.Stop();
                                if (streamInfoHideHandler == null)
                                {
                                    streamInfoHideHandler = (_, __) =>
                                    {
                                        streamInfoHideTimer?.Stop();
                                        streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                                    };
                                    streamInfoHideTimer.Tick += streamInfoHideHandler;
                                }
                                streamInfoHideTimer.Start();
                            }
                        });
                    }
                    catch { }
                }

                async Task StartPlaybackAsync(string url)
                {
                    int consecutiveDownloadFailures = 0;
                    const int MaxDownloadFailures = 5;
                    
                    try
                    {
                        var client = new HttpClientWin();
                        client.DefaultRequestHeaders.Add("Referer", refererUrl);
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                        // Try standard creation first
                        var uri = new Uri(url);
                        var adaptiveResult = await AdaptiveMediaSource.CreateFromUriAsync(uri, client);

                        if (adaptiveResult.Status == AdaptiveMediaSourceCreationStatus.UnsupportedManifestContentType)
                        {
                            // Fallback: Download manifest manually and force content type
                            var response = await client.GetAsync(uri);
                            response.EnsureSuccessStatusCode();
                            var stream = await response.Content.ReadAsInputStreamAsync();
                            adaptiveResult = await AdaptiveMediaSource.CreateFromStreamAsync(stream, uri, "application/vnd.apple.mpegurl");
                        }

                        if (adaptiveResult.Status != AdaptiveMediaSourceCreationStatus.Success || adaptiveResult.MediaSource == null)
                        {
                            throw new InvalidOperationException($"Adaptive source failed: {adaptiveResult.Status}");
                        }

                        // Attach handler to fix segment content types and ensure headers
                        adaptiveResult.MediaSource.DownloadRequested += async (sender, args) =>
                        {
                            // Intercept Manifest, MediaSegment, and InitializationSegment
                            if (args.ResourceType == AdaptiveMediaSourceResourceType.Manifest ||
                                args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment ||
                                args.ResourceType == AdaptiveMediaSourceResourceType.InitializationSegment)
                            {
                                var deferral = args.GetDeferral();
                                try
                                {
                                    var request = new global::Windows.Web.Http.HttpRequestMessage(global::Windows.Web.Http.HttpMethod.Get, args.ResourceUri);
                                    request.Headers.TryAppendWithoutValidation("Referer", refererUrl);
                                    request.Headers.TryAppendWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                                    var response = await client.SendRequestAsync(request);
                                    response.EnsureSuccessStatusCode();

                                    var contentType = response.Content.Headers.ContentType?.ToString();
                                    var path = args.ResourceUri.AbsolutePath;

                                    // Force correct content types
                                    if (args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment)
                                    {
                                        // If it's a media segment, force video/MP2T if it's not a valid video type
                                        // Many servers return text/plain or application/octet-stream for .ts
                                        if (string.IsNullOrEmpty(contentType) ||
                                            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                            contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                                            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                                        {
                                            contentType = "video/MP2T";
                                        }
                                    }
                                    else if (args.ResourceType == AdaptiveMediaSourceResourceType.Manifest)
                                    {
                                        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                                        {
                                            contentType = "application/vnd.apple.mpegurl";
                                        }
                                    }

                                    args.Result.InputStream = await response.Content.ReadAsInputStreamAsync();
                                    args.Result.ContentType = contentType;
                                    
                                    // Reset failure counter on successful download
                                    consecutiveDownloadFailures = 0;
                                }
                                catch (Exception ex)
                                {
                                    var statusCode = 0;
                                    try
                                    {
                                        // Windows.Web.Http throws COMException for HTTP errors
                                        // Try to extract status code from exception message or use HResult
                                        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
                                        if (msg.Contains("404")) statusCode = 404;
                                        else if (msg.Contains("502")) statusCode = 502;
                                        else if (msg.Contains("503")) statusCode = 503;
                                        else if (msg.Contains("403")) statusCode = 403;
                                        else if (msg.Contains("401")) statusCode = 401;
                                        else if (msg.Contains("500")) statusCode = 500;
                                    }
                                    catch { }
                                    
                                    System.Diagnostics.Debug.WriteLine($"[Windows] Segment download failed ({args.ResourceType}, status={statusCode}): {ex.Message}");
                                    
                                    consecutiveDownloadFailures++;
                                    
                                    // After max failures, trigger media failure to switch streams
                                    if (consecutiveDownloadFailures >= MaxDownloadFailures)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Windows] Max download failures ({MaxDownloadFailures}) reached, triggering stream failure");
                                        
                                        MainThread.BeginInvokeOnMainThread(() =>
                                        {
                                            try
                                            {
                                                // Clear source to trigger MediaFailed event
                                                mediaPlayer.Source = null;
                                                
                                                // Manually trigger failure handling
                                                Restore();
                                                tcs.TrySetResult(PlaybackResult.Completed($"Stream failed after {MaxDownloadFailures} consecutive download errors (last: {statusCode})", true));
                                            }
                                            catch { }
                                        });
                                    }
            
                                    // Signal error status to Windows Media Player
                                    try
                                    {
                                        args.Result.ExtendedStatus = statusCode > 0 ? (uint)statusCode : 1;
                                    }
                                    catch { }
                                }
                                finally
                                {
                                    deferral.Complete();
                                }
                            }
                        };

                        var mediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptiveResult.MediaSource);
                        var playbackItem = new MediaPlaybackItem(mediaSource);

                        // Ensure UI updates happen on the main thread
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                mediaPlayer.Source = playbackItem;
                                _currentPlaybackItem = playbackItem;

                                // Add mediaPlayerElement to grid now that source is set
                                if (!grid.Children.Contains(mediaPlayerElement))
                                {
                                    grid.Children.Insert(0, mediaPlayerElement); // Insert at index 0 to be behind other elements
                                }

                                // Extract metadata immediately when source is set so orchestrator can get it after 2.5s
                                if (mediaPlayer.Source is MediaPlaybackItem item)
                                {
                                    ExtractVideoMetadata(item, mediaPlayer);
                                    // Update bitrate from adaptive source during playback
                                    UpdateBitrateFromAdaptiveSource(item);
                                }

                                currentPlaybackUrl = url;

                                // Ensure the grid is visible and hit testable
                                grid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                grid.IsHitTestVisible = true;

                                nativeWindow.Content = grid;

                                // Force layout update
                                nativeWindow.Activate();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Windows] Failed to attach playback source: {ex.GetType().Name} - {ex.Message}");
                                Restore();
                                tcs.TrySetResult(PlaybackResult.Completed($"Failed to attach playback source: {ex.Message}", true));
                            }
                        });

                        // Do not set success result here. We wait for user close or media events;
                    }
                    catch (Exception ex)
                    {
                        Restore();
                        System.Diagnostics.Debug.WriteLine($"[Windows] Failed to start playback: {ex.GetType().Name} - {ex.Message}");
                        tcs.TrySetResult(PlaybackResult.Completed($"Failed to start playback: {ex.Message}", true));
                    }
                }

                async Task TrySwitchToCurrentStreamAsync(bool force = false)
                {
                    if (switchingService == null) return;
                    try
                    {
                        var current = switchingService.GetCurrentStream();
                        var url = current?.ResolvedM3U8Url;
                        if (string.IsNullOrWhiteSpace(url)) return;
                        if (!force && string.Equals(currentPlaybackUrl, url, StringComparison.OrdinalIgnoreCase)) return;
                        await StartPlaybackAsync(url);
                    }
                    catch { }
                }

                if (switchingService != null)
                {
                    try
                    {
                        healthyStreamsSubscription = switchingService.HealthyStreamsUpdated.Subscribe(_ => UpdateStreamInfo());
                        currentIndexSubscription = switchingService.CurrentStreamIndexChanged.Subscribe(index =>
                        {
                            UpdateStreamInfo();
                            _ = TrySwitchToCurrentStreamAsync();
                        });
                        UpdateStreamInfo();
                    }
                    catch { }
                }

                try
                {
                    var enriched = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IEnrichedGameService)) as VardyParty.Services.IEnrichedGameService;
                    if (enriched != null)
                    {
                        gamesSubscription = enriched.GamesStream.Subscribe(dict =>
                        {
                            if (dict == null) return;
                            lock (gamesLock)
                            {
                                latestGamesByLeague = dict.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                            }

                            if (isScoresTickerVisible)
                            {
                                MainThread.BeginInvokeOnMainThread(() => RefreshTickerText(resetOffset: false));
                            }
                        });
                    }
                }
                catch { }

                // Intercept the title-bar X button so it stops playback instead of killing the app
                try
                {
                    if (nativeWindow?.AppWindow != null)
                    {
                        appWindowClosingHandler = (_, args) =>
                        {
                            // Unhook synchronously first so subsequent close attempts work normally
                            try
                            {
                                nativeWindow.AppWindow.Closing -= appWindowClosingHandler;
                                appWindowClosingHandler = null;
                            }
                            catch { }

                            args.Cancel = true;
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                try
                                {
                                    var svc = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                                    svc?.Cleanup();
                                }
                                catch { }
                                try
                                {
                                    streamResolutionOrchestrator?.Reset();
                                }
                                catch { }
                                Restore();
                                tcs.TrySetResult(PlaybackResult.SuccessResult("User closed video player"));
                            });
                        };
                        nativeWindow.AppWindow.Closing += appWindowClosingHandler;
                    }
                }
                catch { }

                // Ensure we restore and cleanup if the window is closed externally
                try
                {
                    var window = nativeWindow;
                    if (window != null)
                    {
                        window.Closed += (s, e) =>
                        {
                            try
                            {
                                var svc = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                                svc?.Cleanup();
                            }
                            catch { }
                            try { Restore(); } catch { }
                            try { tcs.TrySetResult(PlaybackResult.Completed("Window closed", true)); } catch { }
                        };
                    }
                }
                catch { }


                _ = StartPlaybackAsync(m3u8Url);
            });

            return tcs.Task;
        }

        public class VardyPartyWindow : MauiWinUIWindow
        {
        }

        private void ExtractVideoMetadata(MediaPlaybackItem mediaItem, MediaPlayer player)
        {
            try
            {
                var metrics = new PlaybackMetrics();

                // Get actual playback resolution from the session (not encoding properties)
                // For HLS adaptive streams, encoding properties return max variant, not actual playing resolution
                uint actualWidth = 0;
                uint actualHeight = 0;
                
                try
                {
                    // Get from the media player's playback session for accurate current resolution
                    if (player?.PlaybackSession != null)
                    {
                        actualWidth = player.PlaybackSession.NaturalVideoWidth;
                        actualHeight = player.PlaybackSession.NaturalVideoHeight;
                    }
                }
                catch { }

                if (mediaItem.VideoTracks.Count > 0)
                {
                    var videoTrack = mediaItem.VideoTracks[0];
                    var videoProps = videoTrack.GetEncodingProperties();

                    // Extract resolution - prefer PlaybackSession over encoding properties for HLS
                    if (actualWidth > 0 && actualHeight > 0)
                    {
                        metrics.Resolution = ((int)actualWidth, (int)actualHeight);
                        System.Diagnostics.Debug.WriteLine($"[Windows] Resolution from PlaybackSession: {actualWidth}x{actualHeight}");
                    }
                    else if (videoProps.Width > 0 && videoProps.Height > 0)
                    {
                        metrics.Resolution = ((int)videoProps.Width, (int)videoProps.Height);
                        System.Diagnostics.Debug.WriteLine($"[Windows] Resolution from encoding properties: {videoProps.Width}x{videoProps.Height} (may be max variant, not actual)");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] Resolution not available (Session: {actualWidth}x{actualHeight}, Props: {videoProps.Width}x{videoProps.Height})");
                    }

                    // Extract framerate - often unavailable for HLS streams
                    if (videoProps.FrameRate.Numerator > 0 && videoProps.FrameRate.Denominator > 0)
                    {
                        var fps = (int)(videoProps.FrameRate.Numerator / (double)videoProps.FrameRate.Denominator);
                        if (fps > 0)
                        {
                            metrics.Framerate = fps;
                            System.Diagnostics.Debug.WriteLine($"[Windows] Framerate: {fps} fps ({videoProps.FrameRate.Numerator}/{videoProps.FrameRate.Denominator})");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[Windows] Framerate calculation resulted in 0 ({videoProps.FrameRate.Numerator}/{videoProps.FrameRate.Denominator})");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] Framerate not available from encoding properties (expected for HLS adaptive streams)");
                    }

                    // Extract video codec
                    //metrics.VideoCodec = CodecSubtypeToFriendlyName(videoProps.Subtype);
                    
                    // Extract bitrate - typically 0 for HLS (use AdaptiveMediaSource instead)
                    if (videoProps.Bitrate > 0)
                    {
                        metrics.BitrateKbps = (int)(videoProps.Bitrate / 1000);
                        System.Diagnostics.Debug.WriteLine($"[Windows] Bitrate from encoding properties: {metrics.BitrateKbps} kbps");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] Bitrate not in encoding properties (will use AdaptiveMediaSource.CurrentDownloadBitrate)");
                    }
                }

                // Extract audio track information
                if (mediaItem.AudioTracks.Count > 0)
                {
                    var audioTrack = mediaItem.AudioTracks[0];
                    var audioProps = audioTrack.GetEncodingProperties();

                    // Extract audio codec
                }

                _currentMetrics = metrics;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Windows] Failed to extract video metadata: {ex.Message}");
            }
        }

        private void UpdateBitrateFromAdaptiveSource(MediaPlaybackItem? mediaItem)
        {
            // For HLS/DASH streams, bitrate comes from AdaptiveMediaSource during playback, not encoding properties
            if (mediaItem?.Source is MediaSource ms && ms.AdaptiveMediaSource != null && _currentMetrics != null)
            {
                var ams = ms.AdaptiveMediaSource;
                if (ams.CurrentDownloadBitrate > 0)
                {
                    _currentMetrics.BitrateKbps = (int)(ams.CurrentDownloadBitrate / 1000);
                    System.Diagnostics.Debug.WriteLine($"[Windows] Bitrate from AdaptiveMediaSource: {_currentMetrics.BitrateKbps} kbps");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] AdaptiveMediaSource.CurrentDownloadBitrate not available yet: {ams.CurrentDownloadBitrate}");
                }
            }
            else
            {
                var hasMediaItem = mediaItem != null;
                var hasMediaSource = mediaItem?.Source != null;
                var hasAMS = mediaItem?.Source is MediaSource msCheck && msCheck.AdaptiveMediaSource != null;
                var hasMetrics = _currentMetrics != null;
                System.Diagnostics.Debug.WriteLine($"[Windows] Cannot update bitrate - mediaItem={hasMediaItem}, MediaSource={hasMediaSource}, AMS={hasAMS}, metrics={hasMetrics}");
            }
        }
    }

    internal enum WindowsScoresTickerMode
    {
        SameLeagueInPlay,
        AllLeaguesInPlay,
        AllFinished,
        AllUpcoming
    }
}
