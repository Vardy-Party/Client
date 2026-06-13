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
using VardyParty.Extensions;
using VardyParty.Health;
using VardyParty.Models;
using System.Text.RegularExpressions;

namespace VardyParty.Platforms.Windows
{
    public class WindowsVideoPlayerService : INativeVideoPlayerService
    {
        // Segoe UI renders regional-indicator pairs as plain letters; strip them for display.
        // \p{Regional_Indicator} is unavailable on some .NET Windows builds — use UTF-16 ranges.
        private static readonly Regex TickerMeasurePlainTextRegex = new(
            @"\uD83C[\uDDE6-\uDDFF](?:\uD83C[\uDDE6-\uDDFF])?",
            RegexOptions.Compiled);

        private static string ToTickerDisplayText(string text) =>
            TickerMeasurePlainTextRegex.Replace(text, string.Empty);

        private static string TruncateForLog(string text, int maxLength) =>
            text.Length <= maxLength ? text : text[..maxLength] + "…";

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

            // Block Blazor renders before any UI-thread work is queued — progress updates can
            // still fire on a background thread after the first healthy stream is found.
            MainPage.SetNativePlayerActive(true);
            WindowsEventLogger.Info("VideoPlayer", $"PlayVideoAsync starting: {title}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                WindowsEventLogger.Info("VideoPlayer", "UI thread: building player chrome");
                // Try to get the window from the Current application windows
                var mauiWindow = MauiApp.Current?.Windows.FirstOrDefault() ?? MauiApp.Current?.Windows.FirstOrDefault();
                var nativeWindow = mauiWindow?.Handler?.PlatformView as MauiWinUIWindow;

                if (nativeWindow == null)
                {
                    MainPage.SetNativePlayerActive(false);
                    tcs.TrySetResult(PlaybackResult.Completed("No window available for playback.", true));
                    return;
                }

                var originalContent = nativeWindow.Content;
                var playerOverlayAttached = false;
                WinGrid? playerGrid = null;

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

                static bool IsInteractiveSource(object? source)
                {
                    return source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                        || source is Microsoft.UI.Xaml.Controls.Slider
                        || source is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                        || source is Microsoft.UI.Xaml.Controls.ComboBox
                        || source is Microsoft.UI.Xaml.Controls.TextBox;
                }

                static bool IsVideoSurfaceHit(object? source, MediaPlayerElement playerElement)
                {
                    // Pointer events are already attached to mediaPlayerElement. We only need to reject
                    // obvious interactive controls so video-surface interactions still work.
                    return !IsInteractiveSource(source);
                }

                // Double-click on video surface toggles fullscreen/windowed.
                mediaPlayerElement.DoubleTapped += (_, e) =>
                {
                    try
                    {
                        if (!IsVideoSurfaceHit(e.OriginalSource, mediaPlayerElement)) return;

                        var appWindow = nativeWindow.AppWindow;
                        if (appWindow == null) return;

                        var isFullScreen = appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
                        appWindow.SetPresenter(
                            isFullScreen
                                ? Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped
                                : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                    }
                    catch { }
                };

                WindowsWindowDragHelper.AttachPointerDrag(
                    mediaPlayerElement,
                    nativeWindow,
                    (source, _) => IsVideoSurfaceHit(source, mediaPlayerElement));

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
                string? lastStreamVerticalResolution = null;
                bool isScoresTickerVisible = false;
                List<TickerDisplayPart>? scoresTickerSingleCopy = null;
                string scoresTickerPlainPreview = string.Empty;
                double scoresTickerOffsetPx = 0;
                double tickerMeasuredTextWidth = 0;   // full track width (one or two copies)
                double tickerLoopWidth = 0;            // scroll loop width (single copy + separator when looping)
                bool scoresTickerLoopEnabled = false;
                int tickerScrollDelayTicks = 0;
                bool tickerUserPaused = false;         // true while user is dragging or hovering after drag
                int tickerResumeCountdown = 0;         // counts down 60fps ticks after pointer leaves
                const int TickerReadDelayTicks = 180;  // ~3 seconds at 60fps before scrolling starts
                const int TickerResumeDelayTicks = 180; // ~3 seconds after pointer-exit before resuming
                const double tickerSpeedPerTickPx = 1.5;
                Dictionary<string, List<Game>>? latestGamesByLeague = null;
                var gamesLock = new object();
                var scoresTickerMode = WindowsScoresTickerMode.SameLeagueInPlay;

                static string? StripTickerFlags(string? value) =>
                    string.IsNullOrWhiteSpace(value)
                        ? null
                        : TickerMeasurePlainTextRegex.Replace(value, string.Empty).Trim();

                (string? Home, string? Away, string? League) ResolveWatchedContext()
                {
                    var resolvedHome = StripTickerFlags(homeTeam);
                    var resolvedAway = StripTickerFlags(awayTeam);
                    var resolvedLeague = string.IsNullOrWhiteSpace(league) ? null : league.Trim();

                    if (!string.IsNullOrEmpty(resolvedHome) && !string.IsNullOrEmpty(resolvedAway))
                    {
                        return (resolvedHome, resolvedAway, resolvedLeague);
                    }

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var idx = title.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            resolvedHome = StripTickerFlags(title[..idx]);
                            resolvedAway = StripTickerFlags(title[(idx + 4)..]);
                        }
                    }

                    return (resolvedHome, resolvedAway, resolvedLeague);
                }

                var watchedContext = ResolveWatchedContext();
                var watchedHomeTeam = watchedContext.Home;
                var watchedAwayTeam = watchedContext.Away;
                var watchedLeagueName = watchedContext.League;

                TypedEventHandler<MediaPlaybackSession, object>? playbackStateChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? naturalVideoSizeChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? positionChangedHandler = null;
                TypedEventHandler<MediaPlayer, object>? mediaEndedHandler = null;
                TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailedHandler = null;
                
                bool metadataReported = false;
                bool isPointerNearNextButton = false;
                bool isNextStreamRequestInProgress = false;
                int playbackGeneration = 0;
                var playbackSwitchLock = new SemaphoreSlim(1, 1);
                IStreamHealthReporter? _healthReporter = null;
                
                // Resolve health reporter
                try
                {
                    _healthReporter = ((((VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamHealthReporter)) as IStreamHealthReporter))));
                }
                catch { }

                void StopTickerScroll()
                {
                    try { scoresTickerScrollTimer?.Stop(); } catch { }
                }

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
                        try { playbackSwitchLock.Dispose(); } catch { }
                        StopTickerScroll();
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
                // Top-left hamburger button — true top-left anchor, always visible in player
                var menuButton = new WinButton
                {
                    Content = "☰",
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(12, 12, 0, 0),
                    Width = 42,
                    Height = 42,
                    Opacity = 1,
                    Visibility = Microsoft.UI.Xaml.Visibility.Visible,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4)
                };

                // Menu panel opens downward from the button
                var menuPanel = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(12, 62, 0, 0),
                    MinWidth = 180,
                    Spacing = 2,
                    Padding = new WinThickness(6, 6, 6, 6),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xF2, 0x18, 0x18, 0x18)),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new WinThickness(1)
                };

                WinButton MakeMenuItem(string label)
                {
                    var btn = new WinButton
                    {
                        Content = label,
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        HorizontalContentAlignment = WinHorizontalAlignment.Left,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                        BorderThickness = new WinThickness(0),
                        Padding = new WinThickness(12, 8, 12, 8),
                        FontSize = 14,
                        CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4)
                    };
                    return btn;
                }

                var videoInfoButton       = MakeMenuItem("Video Info");
                var sameLeagueTickerButton = MakeMenuItem("Scores");
                var alwaysOnTopButton     = MakeMenuItem("📌 Always on top: Off");
                var reportStreamButton    = MakeMenuItem("Report stream");
                var reportStatusText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = "Reporting stream...",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    FontSize = 12,
                    Margin = new WinThickness(12, 2, 12, 4)
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
                    Padding = new WinThickness(12, 6, 0, 6),
                    MinHeight = 36,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    IsHitTestVisible = true
                };
                var scoresTickerGrid = new WinGrid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    MinHeight = 28
                };
                scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
                // Canvas viewport: unlike Grid, Canvas does NOT constrain child width,
                // so the TextBlock lays out at its full natural width and the clip handles visibility.
                var scoresTickerViewport = new Microsoft.UI.Xaml.Controls.Canvas
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Stretch,
                    MinHeight = 24,
                    Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry()
                };
                WinGrid.SetColumn(scoresTickerViewport, 0);
                var scoresTickerTrack = WindowsScoresTickerTrackBuilder.CreateTrack();

                void LayoutScoresTicker()
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

                void RebuildTickerTrackForViewport()
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

                    var needsLoop = WindowsScoresTickerTrackBuilder.ShouldLoopForScroll(
                        singleCopyWidth,
                        viewportWidth);
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

                void SyncTickerScrollTimer()
                {
                    if (!isScoresTickerVisible)
                    {
                        return;
                    }

                    var viewportWidth = scoresTickerViewport.ActualWidth;
                    if (!scoresTickerLoopEnabled || tickerLoopWidth <= 0 || tickerLoopWidth <= viewportWidth)
                    {
                        StopTickerScroll();
                        return;
                    }

                    EnsureTickerTimer();
                    try { scoresTickerScrollTimer?.Start(); } catch { }
                }

                void SyncTickerLayout()
                {
                    if (!isScoresTickerVisible) return;
                    RebuildTickerTrackForViewport();
                    LayoutScoresTicker();
                    UpdateTickerMeasurements();
                    SyncTickerScrollTimer();
                }

                scoresTickerTrack.Loaded += (_, __) => SyncTickerLayout();
                scoresTickerViewport.SizeChanged += (_, __) => SyncTickerLayout();
                scoresTickerBorder.SizeChanged += (_, __) => SyncTickerLayout();

                // Gesture scroll: pause auto-scroll and let user drag/swipe the text.
                // On Windows, two-finger touchpad horizontal scroll arrives as PointerWheelChanged
                // (horizontal delta), and touch/pen arrives as ManipulationDelta.
                scoresTickerViewport.ManipulationMode = Microsoft.UI.Xaml.Input.ManipulationModes.TranslateX;
                // Canvas needs a background to be a hit-test target
                scoresTickerViewport.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

                scoresTickerViewport.ManipulationDelta += (_, args) =>
                {
                    tickerUserPaused = true;
                    tickerResumeCountdown = 0;

                    if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
                    {
                        scoresTickerOffsetPx += args.Delta.Translation.X;
                        var maxLeft = tickerLoopWidth > 0 ? -tickerLoopWidth : 0.0;
                        scoresTickerOffsetPx = Math.Clamp(scoresTickerOffsetPx, maxLeft, 0.0);
                        t.X = scoresTickerOffsetPx;
                    }
                };

                // Two-finger touchpad horizontal scroll (PointerWheelChanged with horizontal delta).
                // Hook on both the border and the viewport to ensure the event is captured
                // regardless of which element the pointer is over.
                void HandleTickerWheel(object? sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
                {
                    var props = args.GetCurrentPoint(scoresTickerViewport).Properties;

                    if (!props.IsHorizontalMouseWheel) return;

                    var delta = props.MouseWheelDelta;
                    if (delta == 0) return;

                    // Only meaningful if text extends beyond viewport
                    if (tickerLoopWidth <= 0 || tickerLoopWidth <= scoresTickerViewport.ActualWidth) return;

                    // -1 = actively interacting; countdown only starts on PointerExited
                    tickerUserPaused = true;
                    tickerResumeCountdown = -1;

                    if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
                    {
                        // Positive delta = swiped right = text should move right = offset increases
                        scoresTickerOffsetPx -= delta / 120.0 * 50.0;
                        var maxLeft = tickerLoopWidth > 0 ? -tickerLoopWidth : 0.0;
                        scoresTickerOffsetPx = Math.Clamp(scoresTickerOffsetPx, maxLeft, 0.0);
                        t.X = scoresTickerOffsetPx;
                    }

                    args.Handled = true;
                }

                scoresTickerBorder.PointerWheelChanged += HandleTickerWheel;
                scoresTickerViewport.PointerWheelChanged += HandleTickerWheel;

                // When pointer leaves the ticker, start the resume countdown
                scoresTickerBorder.PointerExited += (_, __) =>
                {
                    if (tickerUserPaused && tickerResumeCountdown == -1)
                        tickerResumeCountdown = TickerResumeDelayTicks;
                };
                // While pointer is over the ticker, keep the countdown frozen
                scoresTickerBorder.PointerEntered += (_, __) =>
                {
                    if (tickerUserPaused)
                        tickerResumeCountdown = -1;
                };
                var tickerCycleButton = new WinButton
                {
                    Content = "⟳",
                    FontSize = 16,
                    MinWidth = 40,
                    MinHeight = 28,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new WinThickness(0),
                    Padding = new WinThickness(10, 2, 10, 2),
                    VerticalAlignment = WinVerticalAlignment.Center,
                    IsHitTestVisible = true
                };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tickerCycleButton, "Next scores view (in-play → all live → finished → upcoming)");
                WinGrid.SetColumn(tickerCycleButton, 1);
                scoresTickerViewport.Children.Add(scoresTickerTrack);
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

                // Info overlay — top-right so broadcaster score bugs (usually top-left) stay visible.
                var infoPanel = new Microsoft.UI.Xaml.Controls.Grid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(0, 48, 10, 0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.85 },
                    Padding = new WinThickness(10),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    BorderThickness = new WinThickness(1),
                    MaxWidth = 420
                };
                infoPanel.RowDefinitions.Add(new Microsoft.UI.Xaml.Controls.RowDefinition { Height = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
                infoPanel.RowDefinitions.Add(new Microsoft.UI.Xaml.Controls.RowDefinition { Height = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });

                var infoHeader = new WinGrid { Margin = new WinThickness(0, 0, 0, 8) };
                infoHeader.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                infoHeader.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });

                var infoTitle = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = "Video Info",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 13,
                    VerticalAlignment = WinVerticalAlignment.Center
                };
                WinGrid.SetColumn(infoTitle, 0);

                var infoCloseButton = new WinButton
                {
                    Content = "✕",
                    FontSize = 12,
                    MinWidth = 28,
                    MinHeight = 24,
                    Padding = new WinThickness(6, 0, 6, 0),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new WinThickness(0),
                    VerticalAlignment = WinVerticalAlignment.Center
                };
                WinGrid.SetColumn(infoCloseButton, 1);
                infoHeader.Children.Add(infoTitle);
                infoHeader.Children.Add(infoCloseButton);
                WinGrid.SetRow(infoHeader, 0);
                infoPanel.Children.Add(infoHeader);

                var infoText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12
                };
                WinGrid.SetRow(infoText, 1);
                infoPanel.Children.Add(infoText);

                TypedEventHandler<Microsoft.UI.Windowing.AppWindow, Microsoft.UI.Windowing.AppWindowClosingEventArgs>? appWindowClosingHandler = null;

                bool isClosingPlayer = false;

                void Restore()
                {
                    WindowsEventLogger.Info("VideoPlayer", "Restore: closing native player");
                    // Unhook synchronously so the very next X-press is never cancelled
                    try
                    {
                        if (nativeWindow?.AppWindow != null && appWindowClosingHandler != null)
                        {
                            nativeWindow.AppWindow.Closing -= appWindowClosingHandler;
                            appWindowClosingHandler = null;
                        }
                    }
                    catch { }

                    void DoRestore()
                    {
                        StopTickerScroll();
                        try { scoresTickerTrack.Children.Clear(); } catch { }
                        MainPage.SetNativePlayerActive(false);
                        CleanupMediaPlayer();
                        HidePlayerOverlay();
                        WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow);
                        isClosingPlayer = false;
                    }

                    var queue = nativeWindow?.DispatcherQueue;
                    if (queue != null && queue.HasThreadAccess)
                    {
                        DoRestore();
                    }
                    else
                    {
                        MainThread.BeginInvokeOnMainThread(DoRestore);
                    }
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

                bool IsCurrentGame(Game g)
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

                bool IsWatchedPreMatchFixture(Game g) =>
                    IsCurrentGame(g) && !g.IsPostponed && !g.IsHalfTime && g.Minute is not > 0;

                bool IsUpcomingForTicker(Game g, DateTime nowUtc)
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

                List<TickerDisplayPart> JoinSeparatedLineParts(string header, string emptyMessage, IReadOnlyList<List<TickerDisplayPart>> lines)
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

                List<TickerDisplayPart> FormatUpcomingLineParts(Game g)
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

                List<TickerDisplayPart> FormatWatchedUpcomingFallbackParts(IReadOnlyList<Game> allGames)
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

                List<TickerDisplayPart> FormatInternationalTickerLineParts(Game g, string? statusOverride = null)
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

                List<TickerDisplayPart> BuildSameLeagueTickerParts()
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

                    bool IsSameLeague(Game g)
                    {
                        if (string.IsNullOrWhiteSpace(watchedLeagueName)) return true;
                        return string.Equals((g.DisplayLeague ?? string.Empty).Trim(), watchedLeagueName.Trim(), StringComparison.OrdinalIgnoreCase);
                    }

                    bool IsInPlay(Game g)
                    {
                        if (g.IsFinished || g.IsPostponed) return false;
                        return g.IsInProgress || g.IsHalfTime || g.Minute.HasValue;
                    }

                    var lines = snapshot.Values
                        .SelectMany(v => v)
                        .Where(IsSameLeague)
                        .Where(IsInPlay)
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

                static string BuildAllLeaguesTickerDedupeKey(Game g)
                {
                    var league = (g.DisplayLeague ?? string.Empty).Trim();
                    return $"{league}|{GameMatcher.BuildFixtureKey(g.DisplayHome, g.DisplayAway)}";
                }

                List<TickerDisplayPart> BuildAllLeaguesInPlayTickerParts()
                {
                    List<Game> allGames;
                    lock (gamesLock)
                    {
                        allGames = latestGamesByLeague == null
                            ? new List<Game>()
                            : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                    }

                    var lines = allGames
                        .Where(g => !g.IsFinished && !g.IsPostponed && (g.IsInProgress || g.IsHalfTime || g.Minute.HasValue))
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

                List<TickerDisplayPart> BuildFinishedScoresTickerParts()
                {
                    List<Game> allGames;
                    lock (gamesLock)
                    {
                        allGames = latestGamesByLeague == null
                            ? new List<Game>()
                            : latestGamesByLeague.Values.SelectMany(v => v).ToList();
                    }

                    var lines = allGames
                        .Where(g => g.IsFinished && g.HomeScore.HasValue && g.AwayScore.HasValue)
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

                List<TickerDisplayPart> BuildUpcomingTickerParts()
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

                List<TickerDisplayPart> BuildCurrentModeTickerParts() => scoresTickerMode switch
                {
                    WindowsScoresTickerMode.AllLeaguesInPlay => BuildAllLeaguesInPlayTickerParts(),
                    WindowsScoresTickerMode.AllFinished      => BuildFinishedScoresTickerParts(),
                    WindowsScoresTickerMode.AllUpcoming      => BuildUpcomingTickerParts(),
                    _                                        => BuildSameLeagueTickerParts()
                };

                List<TickerDisplayPart> GetTickerEmptyParts(WindowsScoresTickerMode mode) => mode switch
                {
                    WindowsScoresTickerMode.AllLeaguesInPlay => InternationalTeamDisplay.TextParts("All leagues in-play: No live games right now.").ToList(),
                    WindowsScoresTickerMode.AllFinished      => InternationalTeamDisplay.TextParts("Finished games: No finished games right now.").ToList(),
                    WindowsScoresTickerMode.AllUpcoming      => InternationalTeamDisplay.TextParts("Upcoming games: No unstarted games in the schedule window.").ToList(),
                    _                                        => InternationalTeamDisplay.TextParts(
                        string.IsNullOrWhiteSpace(watchedLeagueName)
                            ? "In-play: No other live games right now."
                            : $"In-play {watchedLeagueName}: No other live games right now.").ToList()
                };

                void EnsureTickerTimer()
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
                                WindowsScoresTickerTrackBuilder.MeasureTrack(
                                    scoresTickerTrack,
                                    Math.Max(scoresTickerViewport.ActualHeight, 24),
                                    out var fullWidth);
                                if (fullWidth <= 0) return;
                                tickerMeasuredTextWidth = fullWidth;
                                tickerLoopWidth = scoresTickerLoopEnabled ? fullWidth / 2.0 : fullWidth;
                            }

                            // Only scroll when a single loop segment is wider than the viewport
                            if (!scoresTickerLoopEnabled || tickerLoopWidth <= viewportWidth)
                            {
                                // Don't reset transform if user is gesturing — let them see what they scrolled to
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
                                        // Countdown complete — resume auto-scroll from current position
                                        tickerUserPaused = false;
                                        // Ensure read-delay branch does not force a reset to X=0
                                        tickerScrollDelayTicks = Math.Max(tickerScrollDelayTicks, TickerReadDelayTicks);
                                    }
                                }
                                // tickerResumeCountdown == -1 means actively interacting, no countdown yet
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

                            // Seamless wrap: once first copy has fully scrolled out, subtract
                            // one loop width so the second copy snaps into the first copy's place
                            if (scoresTickerOffsetPx <= -tickerLoopWidth)
                            {
                                scoresTickerOffsetPx += tickerLoopWidth;
                                // No delay reset — continuous flow, no pause between loops
                            }

                            transform.X = scoresTickerOffsetPx;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Windows] Scores ticker tick failed: {ex.Message}");
                                scoresTickerScrollTimer?.Stop();
                            }
                        };
                        scoresTickerScrollTimer.Tick += scoresTickerScrollHandler;
                    }
                }

                void ApplyTickerParts(IReadOnlyList<TickerDisplayPart> singleCopy, bool resetOffset)
                {
                    StopTickerScroll();

                    scoresTickerSingleCopy = singleCopy.ToList();
                    scoresTickerPlainPreview = InternationalTeamDisplay.PartsToPlainText(singleCopy);
                    WindowsEventLogger.Info(
                        "ScoresTicker",
                        $"mode={scoresTickerMode} parts={singleCopy.Count} preview={TruncateForLog(scoresTickerPlainPreview, 120)}");

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

                void RefreshTickerText(bool resetOffset)
                {
                    List<TickerDisplayPart> parts;
                    try
                    {
                        parts = BuildCurrentModeTickerParts();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] BuildTickerText failed: {ex}");
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
                        System.Diagnostics.Debug.WriteLine($"[Windows] ApplyTickerParts failed: {ex.Message}");
                        var fallback = GetTickerEmptyParts(scoresTickerMode);
                        ApplyTickerParts(fallback, resetOffset);
                    }
                }

                void UpdateTickerMeasurements()
                {
                    try
                    {
                        if (scoresTickerViewport.ActualWidth <= 0 || scoresTickerSingleCopy == null || scoresTickerSingleCopy.Count == 0) return;

                        WindowsScoresTickerTrackBuilder.MeasureTrack(
                            scoresTickerTrack,
                            Math.Max(scoresTickerViewport.ActualHeight, 24),
                            out var fullWidth);
                        if (fullWidth <= 0) return;

                        tickerMeasuredTextWidth = fullWidth;
                        tickerLoopWidth = scoresTickerLoopEnabled ? fullWidth / 2.0 : fullWidth;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] UpdateTickerMeasurements failed: {ex.Message}");
                    }
                }

                void RefreshGamesSnapshot(VardyParty.Services.IEnrichedGameService? enrichedService = null)
                {
                    var service = enrichedService
                        ?? VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IEnrichedGameService)) as VardyParty.Services.IEnrichedGameService;
                    var dict = service?.GetLatestGames();
                    if (dict == null) return;

                    lock (gamesLock)
                    {
                        latestGamesByLeague = dict.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                    }
                }

                void ToggleScoresTicker()
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
                            scoresTickerMode = WindowsScoresTickerMode.SameLeagueInPlay;
                            RefreshTickerText(resetOffset: true);
                        }
                        else
                        {
                            StopTickerScroll();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Windows] ToggleScoresTicker failed: {ex.Message}");
                        isScoresTickerVisible = false;
                        scoresTickerBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
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

                // Stream info display (top right — matches Android overlay placement)
                var streamInfoPanel = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(0, 48, 10, 0),
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

                // Next stream button host (button + x/y hint directly beneath)
                var nextButtonContainer = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Center,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    Spacing = 4,
                    Visibility = Microsoft.UI.Xaml.Visibility.Visible,
                    Opacity = 0,
                    IsHitTestVisible = true
                };

                // Transparent hover target on the right edge — avoids showing the button whenever the cursor is anywhere over fullscreen video.
                var nextButtonHotZone = new Microsoft.UI.Xaml.Controls.Border
                {
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    Width = 200,
                    MinHeight = 260,
                    Padding = new WinThickness(0, 32, 48, 32),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
                };

                // Next stream button — floating right-centre, inset from edge
                var nextButton = new WinButton
                {
                    Content = "⏭",
                    FontSize = 18,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new WinThickness(1),
                    Padding = new WinThickness(14, 10, 14, 10),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
                    HorizontalAlignment = WinHorizontalAlignment.Center,
                    VerticalAlignment = WinVerticalAlignment.Center
                };

                var nextButtonHintText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = WinHorizontalAlignment.Center,
                    TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                    Text = string.Empty,
                    Height = 16,
                    Visibility = Microsoft.UI.Xaml.Visibility.Visible,
                    Opacity = 0,
                    IsHitTestVisible = false
                };

                nextButtonContainer.Children.Add(nextButton);
                nextButtonContainer.Children.Add(nextButtonHintText);
                nextButtonHotZone.Child = nextButtonContainer;

                playerGrid = new WinGrid
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Stretch
                };
                playerGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
                // Don't add mediaPlayerElement here yet - defer until source is initialized
                playerGrid.Children.Add(dismissSurface);
                playerGrid.Children.Add(infoPanel);
                playerGrid.Children.Add(streamInfoPanel);
                playerGrid.Children.Add(menuPanel);
                playerGrid.Children.Add(menuButton);
                playerGrid.Children.Add(nextButtonHotZone);
                playerGrid.Children.Add(scoresTickerBorder);

                void ShowPlayerOverlay()
                {
                    if (ReferenceEquals(nativeWindow.Content, playerGrid))
                    {
                        playerOverlayAttached = true;
                        return;
                    }

                    if (nativeWindow.Content is Microsoft.UI.Xaml.UIElement currentRoot
                        && !ReferenceEquals(currentRoot, playerGrid))
                    {
                        originalContent = currentRoot;
                    }

                    nativeWindow.Content = playerGrid;
                    playerOverlayAttached = true;
                    WindowsEventLogger.Info("VideoPlayer", "Player grid set as window content");
                }

                void HidePlayerOverlay()
                {
                    if (nativeWindow.Content is Microsoft.UI.Xaml.UIElement current
                        && ReferenceEquals(current, playerGrid)
                        && originalContent is Microsoft.UI.Xaml.UIElement restored)
                    {
                        nativeWindow.Content = restored;
                    }

                    playerOverlayAttached = false;
                }

                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(dismissSurface, 40);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(streamInfoPanel, 60);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(scoresTickerBorder, 80);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(infoPanel, 100);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(menuPanel, 110);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(menuButton, 110);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(nextButtonHotZone, 70);

                playerGrid.IsTabStop = true;
                playerGrid.KeyDown += (_, e) =>
                {
                    if (e.Key != global::Windows.System.VirtualKey.Escape) return;

                    if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                    {
                        HideVideoInfoPanel();
                        e.Handled = true;
                        return;
                    }

                    if (menuPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                    {
                        menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        RefreshDismissSurface();
                        e.Handled = true;
                    }
                };

                void HideVideoInfoPanel()
                {
                    infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    RefreshDismissSurface();
                }

                void ShowVideoInfoPanel()
                {
                    streamInfoHideTimer?.Stop();
                    streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    UpdateInfo();
                    RefreshDismissSurface();
                    try { playerGrid.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { }
                }

                infoCloseButton.Click += (_, __) => HideVideoInfoPanel();

                videoInfoButton.Click += (_, __) =>
                {
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                    {
                        HideVideoInfoPanel();
                    }
                    else
                    {
                        ShowVideoInfoPanel();
                    }
                };

                dismissSurface.PointerPressed += (_, __) =>
                {
                    menuPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    HideVideoInfoPanel();
                };

                nextButton.Click += async (_, __) =>
                {
                    if (onNextStreamRequested == null || isNextStreamRequestInProgress || cleanupInvoked)
                        return;

                    isNextStreamRequestInProgress = true;
                    nextButton.IsEnabled = false;
                    try
                    {
                        // SwitchToNextStream inside the callback publishes CurrentStreamIndexChanged,
                        // which triggers TrySwitchToCurrentStreamAsync — do not call it again here.
                        await onNextStreamRequested();
                    }
                    catch (Exception ex)
                    {
                        WindowsEventLogger.Error("VideoPlayer", "Next stream request failed", ex);
                    }
                    finally
                    {
                        isNextStreamRequestInProgress = false;
                        if (!cleanupInvoked)
                            nextButton.IsEnabled = true;
                    }
                };

                // Show/hide Next button only when the cursor is near the right-edge hot zone.
                if (onNextStreamRequested != null)
                {
                    var nextBgNormal = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
                    var nextBgHover  = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x30));
                    void ShowNextButtonChrome()
                    {
                        if (nextButtonHotZone.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
                        nextButtonContainer.Opacity = 1;
                    }

                    void HideNextButtonChrome()
                    {
                        nextButtonContainer.Opacity = 0;
                        nextButton.Background = nextBgNormal;
                        nextButtonHintText.Opacity = 0;
                    }

                    nextButtonHotZone.PointerEntered += (_, __) =>
                    {
                        isPointerNearNextButton = true;
                        ShowNextButtonChrome();
                    };
                    nextButtonHotZone.PointerExited += (_, __) =>
                    {
                        isPointerNearNextButton = false;
                        HideNextButtonChrome();
                    };
                    nextButton.PointerEntered += (_, __) =>
                    {
                        nextButton.Background = nextBgHover;
                        if (nextButtonHotZone.Visibility == Microsoft.UI.Xaml.Visibility.Visible && !string.IsNullOrWhiteSpace(nextButtonHintText.Text))
                        {
                            nextButtonHintText.Opacity = 1;
                        }
                    };
                    nextButton.PointerExited += (_, __) =>
                    {
                        nextButton.Background = nextBgNormal;
                        nextButtonHintText.Opacity = 0;
                    };
                }

                playerGrid.PointerMoved += (s, e) =>
                {
                    try
                    {
                        // Menu button remains fixed and visible at top-left.
                        menuButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        menuButton.Opacity = 1;
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
                        var current = switchingService.GetCurrentStream();

                        string? ExtractVerticalResolution(string? resolution)
                        {
                            if (string.IsNullOrWhiteSpace(resolution)) return null;
                            var match = System.Text.RegularExpressions.Regex.Match(resolution, @"(\d{3,4})\s*[xX]\s*(\d{3,4})");
                            if (match.Success)
                            {
                                return $"{match.Groups[2].Value}p";
                            }
                            return null;
                        }

                        var verticalResolution =
                            ExtractVerticalResolution(current?.Health?.Resolution)
                            ?? ExtractVerticalResolution(current?.Stream?.Resolution)
                            ?? (_currentMetrics?.Resolution is { } r ? $"{r.Item2}p" : null);

                        var hasChanged = total != lastStreamTotal || index != lastStreamIndex;
                        var hasResolutionChanged = !string.Equals(lastStreamVerticalResolution ?? string.Empty, verticalResolution ?? string.Empty, StringComparison.Ordinal);
                        lastStreamTotal = total;
                        lastStreamIndex = index;
                        lastStreamVerticalResolution = verticalResolution;
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                            {
                                return;
                            }

                            if (total > 0)
                            {
                                streamCountText.Text = string.IsNullOrWhiteSpace(verticalResolution)
                                    ? $"Stream: {index}/{total}"
                                    : $"Stream: {index}/{total} ({verticalResolution})";
                            }
                            else
                            {
                                streamCountText.Text = "Streams: 0";
                            }

                            var canSwitchToAnother = onNextStreamRequested != null && total > 1;
                            nextButtonHotZone.Visibility = canSwitchToAnother
                                ? Microsoft.UI.Xaml.Visibility.Visible
                                : Microsoft.UI.Xaml.Visibility.Collapsed;
                            if (!canSwitchToAnother)
                            {
                                isPointerNearNextButton = false;
                                nextButtonContainer.Opacity = 0;
                                nextButtonHintText.Text = string.Empty;
                                nextButtonHintText.Opacity = 0;
                            }
                            else
                            {
                                nextButtonHintText.Text = $"{index}/{total}";
                                nextButtonContainer.Opacity = isPointerNearNextButton ? 1 : 0;
                            }

                            var shouldShowStreamOverlay = total > 0 &&
                                (hasChanged
                                 || hasResolutionChanged
                                 || (streamInfoPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible && !string.IsNullOrWhiteSpace(verticalResolution)));
                            if (shouldShowStreamOverlay)
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

                void PreparePlaybackSwitchOnUiThread(int generation)
                {
                    if (generation != playbackGeneration || cleanupInvoked) return;

                    StopTickerScroll();
                    try
                    {
                        mediaPlayer.Pause();
                        mediaPlayer.Source = null;
                        _currentPlaybackItem = null;
                    }
                    catch { }
                }

                async Task StartPlaybackAsync(string url)
                {
                    await playbackSwitchLock.WaitAsync();
                    var generation = Interlocked.Increment(ref playbackGeneration);
                    int consecutiveDownloadFailures = 0;
                    const int MaxDownloadFailures = 5;

                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(() => PreparePlaybackSwitchOnUiThread(generation));
                        if (generation != playbackGeneration || cleanupInvoked)
                            return;

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
                            if (generation != playbackGeneration || cleanupInvoked)
                                return;

                            // Intercept Manifest, MediaSegment, and InitializationSegment
                            if (args.ResourceType == AdaptiveMediaSourceResourceType.Manifest ||
                                args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment ||
                                args.ResourceType == AdaptiveMediaSourceResourceType.InitializationSegment)
                            {
                                var deferral = args.GetDeferral();
                                try
                                {
                                    if (generation != playbackGeneration || cleanupInvoked)
                                        return;
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
                                    if (consecutiveDownloadFailures >= MaxDownloadFailures
                                        && generation == playbackGeneration
                                        && !cleanupInvoked)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Windows] Max download failures ({MaxDownloadFailures}) reached, triggering stream failure");
                                        
                                        MainThread.BeginInvokeOnMainThread(() =>
                                        {
                                            try
                                            {
                                                if (generation != playbackGeneration || cleanupInvoked)
                                                    return;

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

                        if (generation != playbackGeneration || cleanupInvoked)
                            return;

                        // Ensure UI updates happen on the main thread
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                if (generation != playbackGeneration || cleanupInvoked)
                                    return;

                                mediaPlayer.Source = playbackItem;
                                _currentPlaybackItem = playbackItem;

                                // Add mediaPlayerElement to grid now that source is set
                                if (!playerGrid.Children.Contains(mediaPlayerElement))
                                {
                                    playerGrid.Children.Insert(0, mediaPlayerElement); // Insert at index 0 to be behind other elements
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
                                playerGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                playerGrid.IsHitTestVisible = true;

                                ShowPlayerOverlay();
                                WindowsEventLogger.Info("VideoPlayer", $"Playback source attached for {title}");

                                // Force layout update
                                nativeWindow.Activate();
                            }
                            catch (Exception ex)
                            {
                                if (generation != playbackGeneration || cleanupInvoked)
                                    return;

                                WindowsEventLogger.Error("VideoPlayer", "Failed to attach playback source", ex);
                                System.Diagnostics.Debug.WriteLine($"[Windows] Failed to attach playback source: {ex.GetType().Name} - {ex.Message}");
                                Restore();
                                tcs.TrySetResult(PlaybackResult.Completed($"Failed to attach playback source: {ex.Message}", true));
                            }
                        });

                        // Do not set success result here. We wait for user close or media events;
                    }
                    catch (Exception ex)
                    {
                        if (generation == playbackGeneration && !cleanupInvoked)
                        {
                            WindowsEventLogger.Error("VideoPlayer", "Failed to start playback", ex);
                            Restore();
                            System.Diagnostics.Debug.WriteLine($"[Windows] Failed to start playback: {ex.GetType().Name} - {ex.Message}");
                            tcs.TrySetResult(PlaybackResult.Completed($"Failed to start playback: {ex.Message}", true));
                        }
                    }
                    finally
                    {
                        playbackSwitchLock.Release();
                    }
                }

                async Task TrySwitchToCurrentStreamAsync(bool force = false)
                {
                    if (switchingService == null || cleanupInvoked) return;
                    try
                    {
                        var current = switchingService.GetCurrentStream();
                        var url = current?.ResolvedM3U8Url;
                        if (string.IsNullOrWhiteSpace(url)) return;
                        if (!force && string.Equals(currentPlaybackUrl, url, StringComparison.OrdinalIgnoreCase)) return;
                        WindowsEventLogger.Info("VideoPlayer", $"Switching playback source (force={force})");
                        await StartPlaybackAsync(url);
                    }
                    catch (Exception ex)
                    {
                        WindowsEventLogger.Error("VideoPlayer", "Stream switch failed", ex);
                    }
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
                        RefreshGamesSnapshot(enriched);
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
                            if (isClosingPlayer)
                            {
                                args.Cancel = true;
                                return;
                            }

                            // Unhook synchronously first so subsequent close attempts work normally
                            try
                            {
                                nativeWindow.AppWindow.Closing -= appWindowClosingHandler;
                                appWindowClosingHandler = null;
                            }
                            catch { }

                            // Guard: only cancel if our video grid is still active.
                            if (!playerOverlayAttached || !ReferenceEquals(nativeWindow.Content, playerGrid))
                                return;

                            isClosingPlayer = true;
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


                WindowsEventLogger.Info("VideoPlayer", "UI thread: starting playback task");
                ShowPlayerOverlay();
                _ = StartPlaybackAsync(m3u8Url);
                }
                catch (Exception ex)
                {
                    WindowsEventLogger.Fatal("VideoPlayer", "UI thread setup failed", ex);
                    MainPage.SetNativePlayerActive(false);
                    tcs.TrySetResult(PlaybackResult.Completed($"Player UI failed: {ex.Message}", true));
                }
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
