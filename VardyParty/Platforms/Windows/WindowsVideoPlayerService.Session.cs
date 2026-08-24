using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Playback;
using VardyParty.Services;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using HttpClientWin = Windows.Web.Http.HttpClient;
using MauiApp = Microsoft.Maui.Controls.Application;
using WinButton = Microsoft.UI.Xaml.Controls.Button;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinThickness = Microsoft.UI.Xaml.Thickness;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using VardyParty.Extensions;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private sealed partial class PlayerSession
        {
            private readonly WindowsVideoPlayerService _host;
            private readonly string _m3u8Url;
            private readonly string _refererUrl;
            private readonly string _title;
            private readonly Func<Task>? _onNextStreamRequested;
            private readonly string? _league;
            private readonly string? _homeTeam;
            private readonly string? _awayTeam;
            private readonly IReadOnlyDictionary<string, string>? _requestHeaders;
            private readonly TaskCompletionSource<PlaybackResult> _tcs;

            private bool cleanupInvoked;
            private IDisposable? gamesSubscription;
            private Microsoft.UI.Dispatching.DispatcherQueueTimer? scoresTickerScrollTimer;
            private TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? scoresTickerScrollHandler;
            private bool isScoresTickerVisible;
            private List<TickerDisplayPart>? scoresTickerSingleCopy;
            private string scoresTickerPlainPreview = string.Empty;
            private double scoresTickerOffsetPx;
            private double tickerMeasuredTextWidth;
            private double tickerLoopWidth;
            private bool scoresTickerLoopEnabled;
            private int tickerScrollDelayTicks;
            private bool tickerUserPaused;
            private int tickerResumeCountdown;
            private const int TickerReadDelayTicks = 180;
            private const int TickerResumeDelayTicks = 180;
            private const double tickerSpeedPerTickPx = 1.5;
            private Dictionary<string, List<Game>>? latestGamesByLeague;
            private readonly object gamesLock = new();
            private ScoresTickerMode scoresTickerMode = ScoresTickerMode.SameLeagueInPlay;
            private string? watchedHomeTeam;
            private string? watchedAwayTeam;
            private string? watchedLeagueName;
            private Microsoft.UI.Xaml.Controls.Border scoresTickerBorder = null!;
            private WinGrid scoresTickerGrid = null!;
            private Microsoft.UI.Xaml.Controls.Canvas scoresTickerViewport = null!;
            private Microsoft.UI.Xaml.Controls.StackPanel scoresTickerTrack = null!;
            private WinButton tickerCycleButton = null!;
            private WinButton sameLeagueTickerButton = null!;
            private string currentPlaybackUrl = string.Empty;
            private MauiWinUIWindow? nativeWindow;
            private Microsoft.UI.Xaml.UIElement? originalContent;
            private bool playerOverlayAttached;
            private WinGrid playerGrid = null!;
            private MediaPlayerElement mediaPlayerElement = null!;
            private MediaPlayer mediaPlayer = null!;
            private IStreamSwitchingService switchingService = null!;
            private Microsoft.UI.Dispatching.DispatcherQueueTimer? streamInfoHideTimer;
            private TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? streamInfoHideHandler;
            private int lastStreamTotal = -1;
            private int lastStreamIndex = -1;
            private string? lastStreamVerticalResolution;
            private bool isPointerNearNextButton;
            private bool isNextStreamRequestInProgress;
            private WinButton menuButton = null!;
            private Microsoft.UI.Xaml.Controls.StackPanel menuPanel = null!;
            private WinButton videoInfoButton = null!;
            private WinButton alwaysOnTopButton = null!;
            private WinButton reportStreamButton = null!;
            private Microsoft.UI.Xaml.Controls.TextBlock reportStatusText = null!;
            private Microsoft.UI.Xaml.Controls.Border dismissSurface = null!;
            private Microsoft.UI.Xaml.Controls.Grid infoPanel = null!;
            private Microsoft.UI.Xaml.Controls.TextBlock infoText = null!;
            private WinButton infoCloseButton = null!;
            private Microsoft.UI.Xaml.Controls.StackPanel streamInfoPanel = null!;
            private Microsoft.UI.Xaml.Controls.TextBlock streamCountText = null!;
            private Microsoft.UI.Xaml.Controls.Border streamSourceBadge = null!;
            private Microsoft.UI.Xaml.Controls.TextBlock streamSourceBadgeText = null!;
            private Microsoft.UI.Xaml.Controls.StackPanel nextButtonContainer = null!;
            private Microsoft.UI.Xaml.Controls.Border nextButtonHotZone = null!;
            private WinButton nextButton = null!;
            private Microsoft.UI.Xaml.Controls.TextBlock nextButtonHintText = null!;
            private Microsoft.UI.Xaml.Media.SolidColorBrush nextBgNormal = null!;
            private Microsoft.UI.Xaml.Media.SolidColorBrush nextBgHover = null!;
            private IStreamResolutionOrchestrator streamResolutionOrchestrator = null!;
            private PlaybackSessionController session = null!;
            private DelegatingMediaEngine engine = null!;
            private IDisposable? healthyStreamsSubscription;
            private IDisposable? currentIndexSubscription;
            private TypedEventHandler<MediaPlaybackSession, object>? playbackStateChangedHandler;
            private TypedEventHandler<MediaPlaybackSession, object>? naturalVideoSizeChangedHandler;
            private TypedEventHandler<MediaPlaybackSession, object>? positionChangedHandler;
            private TypedEventHandler<MediaPlayer, object>? mediaEndedHandler;
            private TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailedHandler;
            private bool suppressIndexDrivenSwitch;
            private DateTime lastMetricsRaiseUtc = DateTime.MinValue;
            private SemaphoreSlim playbackSwitchLock = null!;
            private AdaptiveMediaSource? activeAdaptiveMediaSource;
            private TypedEventHandler<AdaptiveMediaSource, AdaptiveMediaSourceDownloadRequestedEventArgs>? activeDownloadHandler;
            private HttpClientWin? activePlaybackClient;
            private TypedEventHandler<Microsoft.UI.Windowing.AppWindow, Microsoft.UI.Windowing.AppWindowClosingEventArgs>? appWindowClosingHandler;
            private bool isClosingPlayer;

            public PlayerSession(
                WindowsVideoPlayerService host,
                string m3u8Url,
                string refererUrl,
                string title,
                Func<Task>? onNextStreamRequested,
                string? league,
                string? homeTeam,
                string? awayTeam,
                IReadOnlyDictionary<string, string>? requestHeaders,
                TaskCompletionSource<PlaybackResult> tcs)
            {
                _host = host;
                _m3u8Url = m3u8Url;
                _refererUrl = refererUrl;
                _title = title;
                _onNextStreamRequested = onNextStreamRequested;
                _league = league;
                _homeTeam = homeTeam;
                _awayTeam = awayTeam;
                _requestHeaders = requestHeaders;
                _tcs = tcs;
            }

            public void Run()
            {
                try
                {
                    _host._logger.LogInformation("UI thread: building player chrome");
                    // Try to get the window from the Current application windows
                    var mauiWindow = MauiApp.Current?.Windows.FirstOrDefault() ?? MauiApp.Current?.Windows.FirstOrDefault();
                    nativeWindow = mauiWindow?.Handler?.PlatformView as MauiWinUIWindow;

                    if (nativeWindow == null)
                    {
                        MainPage.SetNativePlayerActive(false);
                        _tcs.TrySetResult(PlaybackResult.Completed("No window available for playback.", true));
                        return;
                    }

                    originalContent = nativeWindow.Content as Microsoft.UI.Xaml.UIElement;
                    playerOverlayAttached = false;

                    mediaPlayerElement = new MediaPlayerElement
                    {
                        AreTransportControlsEnabled = true,
                        AutoPlay = true,
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        VerticalAlignment = WinVerticalAlignment.Stretch
                    };

                    mediaPlayer = new MediaPlayer();
                    mediaPlayerElement.SetMediaPlayer(mediaPlayer);
                    currentPlaybackUrl = _m3u8Url;

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
                        catch (Exception ex) { _host.LogIgnored("ToggleFullScreen", ex); }
                    };

                    WindowsWindowDragHelper.AttachPointerDrag(
                        mediaPlayerElement,
                        nativeWindow,
                        (source, _) => IsVideoSurfaceHit(source, mediaPlayerElement));

                    switchingService = _host._switchingService;
                    streamResolutionOrchestrator = _host._services.GetRequiredService<IStreamResolutionOrchestrator>();
                    session = new PlaybackSessionController();
                    engine = new DelegatingMediaEngine();
                    engine.MetricsHandler = () => _host.GetCurrentMetrics();


                    var watchedContext = ResolveWatchedContext();
                    watchedHomeTeam = watchedContext.Home;
                    watchedAwayTeam = watchedContext.Away;
                    watchedLeagueName = watchedContext.League;

                    playbackSwitchLock = new SemaphoreSlim(1, 1);



                    // Top-left hamburger button — true top-left anchor, always visible in player
                    menuButton = new WinButton
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
                    menuPanel = new Microsoft.UI.Xaml.Controls.StackPanel
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

                    videoInfoButton = MakeMenuItem("Video Info");
                    sameLeagueTickerButton = MakeMenuItem("Scores");
                    alwaysOnTopButton = MakeMenuItem("📌 Always on top: Off");
                    reportStreamButton = MakeMenuItem("Report stream");
                    reportStatusText = new Microsoft.UI.Xaml.Controls.TextBlock
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

                    scoresTickerBorder = new Microsoft.UI.Xaml.Controls.Border
                    {
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        VerticalAlignment = WinVerticalAlignment.Bottom,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.80 },
                        Padding = new WinThickness(12, 6, 0, 6),
                        MinHeight = 36,
                        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                        IsHitTestVisible = true
                    };
                    scoresTickerGrid = new WinGrid
                    {
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        VerticalAlignment = WinVerticalAlignment.Center,
                        MinHeight = 28
                    };
                    scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                    scoresTickerGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
                    // Canvas viewport: unlike Grid, Canvas does NOT constrain child width,
                    // so the TextBlock lays out at its full natural width and the clip handles visibility.
                    scoresTickerViewport = new Microsoft.UI.Xaml.Controls.Canvas
                    {
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        VerticalAlignment = WinVerticalAlignment.Stretch,
                        MinHeight = 24,
                        Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry()
                    };
                    WinGrid.SetColumn(scoresTickerViewport, 0);
                    scoresTickerTrack = WindowsScoresTickerTrackBuilder.CreateTrack();


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
                        tickerResumeCountdown = -1;

                        if (scoresTickerTrack.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
                        {
                            scoresTickerOffsetPx = scoresTickerLoopEnabled && tickerLoopWidth > 0
                                ? TickerMarquee.Wrap(scoresTickerOffsetPx + args.Delta.Translation.X, tickerLoopWidth)
                                : Math.Clamp(scoresTickerOffsetPx + args.Delta.Translation.X, tickerLoopWidth > 0 ? -tickerLoopWidth : 0.0, 0.0);
                            t.X = scoresTickerOffsetPx;
                        }
                    };

                    // Two-finger touchpad horizontal scroll (PointerWheelChanged with horizontal delta).
                    // Hook on both the border and the viewport to ensure the event is captured
                    // regardless of which element the pointer is over.

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
                    tickerCycleButton = new WinButton
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
                    dismissSurface = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        HorizontalAlignment = WinHorizontalAlignment.Stretch,
                        VerticalAlignment = WinVerticalAlignment.Stretch,
                        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
                    };

                    // Info overlay — top-right so broadcaster score bugs (usually top-left) stay visible.
                    infoPanel = new Microsoft.UI.Xaml.Controls.Grid
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

                    infoCloseButton = new WinButton
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

                    infoText = new Microsoft.UI.Xaml.Controls.TextBlock
                    {
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12
                    };
                    WinGrid.SetRow(infoText, 1);
                    infoPanel.Children.Add(infoText);




                    tickerCycleButton.Click += (_, __) => CycleScoresTickerMode();


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
                        catch (Exception ex) { _host.LogIgnored("ToggleAlwaysOnTop", ex); }
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
                        catch (Exception ex)
                        {
                            _host.LogIgnored("ReportCurrentStreamAsBad", ex);
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

                    mediaEndedHandler = (s, e) => { Restore(); _tcs.TrySetResult(PlaybackResult.Completed("Stream ended.", false)); };



                    mediaFailedHandler = (s, e) =>
                    {
                        try
                        {
                            if (cleanupInvoked) return;

                            var errMsg = e?.ErrorMessage ?? "Unknown media error";
                            var ext = string.Empty;
                            try { ext = e?.ExtendedErrorCode?.Message ?? string.Empty; } catch (Exception ex) { _host._logger.LogWarning(ex, "Failed to read extended media error"); }
                            var detail = $"Media failed: {errMsg}\n{ext}";
                            engine.Raise(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, detail));
                        }
                        catch (Exception ex)
                        {
                            _host._logger.LogError(ex, "MediaFailed handler failed");
                            engine.Raise(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "Stream error on Windows player."));
                        }
                    };

                    // Detect buffering state from PlaybackSession
                    playbackStateChangedHandler = (playbackSession, _) =>
                    {
                        var isBuffering = playbackSession.PlaybackState == MediaPlaybackState.Buffering;
                        _host.BufferingStateChanged?.Invoke(_host, isBuffering);
                        engine.Raise(MediaEngineEvent.Buffering(session.Snapshot.AttachGeneration, isBuffering));
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
                            _host.ExtractVideoMetadata(item, mediaPlayer);
                            _host.UpdateBitrateFromAdaptiveSource(item);  // Refresh bitrate
                        }
                        UpdateInfo();
                    };

                    positionChangedHandler = (s, e) =>
                    {
                        try
                        {
                            if (mediaPlayer.Source is MediaPlaybackItem item)
                            {
                                _host.UpdateBitrateFromAdaptiveSource(item);
                            }

                            if ((DateTime.UtcNow - lastMetricsRaiseUtc).TotalSeconds >= 30)
                            {
                                lastMetricsRaiseUtc = DateTime.UtcNow;
                                var metrics = _host.GetCurrentMetrics();
                                engine.Raise(MediaEngineEvent.Metrics(
                                    session.Snapshot.AttachGeneration,
                                    metrics?.BitrateKbps,
                                    metrics?.IsBuffering == true));
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _host._logger.LogWarning(ex, "Position handler error");
                        }

                        try
                        {
                            UpdateInfo();
                        }
                        catch (Exception ex)
                        {
                            _host._logger.LogWarning(ex, "UpdateInfo failed");
                        }
                    };

                    mediaPlayer.PlaybackSession.NaturalVideoSizeChanged += naturalVideoSizeChangedHandler;
                    mediaPlayer.PlaybackSession.PositionChanged += positionChangedHandler;

                    // Stream info display (top right — matches Android overlay placement)
                    streamInfoPanel = new Microsoft.UI.Xaml.Controls.StackPanel
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

                    streamCountText = new Microsoft.UI.Xaml.Controls.TextBlock
                    {
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Text = ""
                    };
                    streamInfoPanel.Children.Add(streamCountText);

                    streamSourceBadge = new Microsoft.UI.Xaml.Controls.Border
                    {
                        HorizontalAlignment = WinHorizontalAlignment.Right,
                        CornerRadius = new Microsoft.UI.Xaml.CornerRadius(999),
                        Padding = new WinThickness(6, 1, 6, 1),
                        Margin = new WinThickness(0, 4, 0, 0),
                        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
                    };
                    streamSourceBadgeText = new Microsoft.UI.Xaml.Controls.TextBlock
                    {
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold
                    };
                    streamSourceBadge.Child = streamSourceBadgeText;
                    streamInfoPanel.Children.Add(streamSourceBadge);

                    // Next stream button host (button + x/y hint directly beneath)
                    nextButtonContainer = new Microsoft.UI.Xaml.Controls.StackPanel
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
                    nextButtonHotZone = new Microsoft.UI.Xaml.Controls.Border
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
                    nextButton = new WinButton
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

                    nextButtonHintText = new Microsoft.UI.Xaml.Controls.TextBlock
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
                        if (_onNextStreamRequested == null || isNextStreamRequestInProgress || cleanupInvoked)
                            return;

                        isNextStreamRequestInProgress = true;
                        nextButton.IsEnabled = false;
                        try
                        {
                            // SwitchToNextStream inside the callback publishes CurrentStreamIndexChanged,
                            // which triggers TrySwitchToCurrentStreamAsync — do not call it again here.
                            await _onNextStreamRequested();
                        }
                        catch (Exception ex)
                        {
                            _host._logger.LogError(ex, "Next stream request failed");
                        }
                        finally
                        {
                            isNextStreamRequestInProgress = false;
                            if (!cleanupInvoked)
                                nextButton.IsEnabled = true;
                        }
                    };

                    // Show/hide Next button only when the cursor is near the right-edge hot zone.
                    if (_onNextStreamRequested != null)
                    {
                        nextBgNormal = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
                        nextBgHover = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x30));

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
                        catch (Exception ex) { _host.LogIgnored("ShowMenuButtonOnPointerMoved", ex); }
                    };




                    engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
                    engine.AttachHandler = (url, _, _) => StartPlaybackAsync(url);


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
                        catch (Exception ex) { _host.LogIgnored("SubscribeStreamSwitching", ex); }
                    }

                    try
                    {
                        var enriched = _host._enrichedGames;
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
                    catch (Exception ex) { _host.LogIgnored("SubscribeEnrichedGames", ex); }

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
                                        _host._switchingService.Cleanup();
                                    }
                                    catch (Exception ex) { _host.LogIgnored("CleanupOnAppWindowClosing", ex); }
                                    try
                                    {
                                        streamResolutionOrchestrator?.Reset();
                                    }
                                    catch (Exception ex) { _host.LogIgnored("ResetOrchestratorOnClose", ex); }
                                    Restore();
                                    _tcs.TrySetResult(PlaybackResult.SuccessResult("User closed video player"));
                                });
                            };
                            nativeWindow.AppWindow.Closing += appWindowClosingHandler;
                        }
                    }
                    catch (Exception ex) { _host.LogIgnored("HookAppWindowClosing", ex); }

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
                                    _host._switchingService.Cleanup();
                                }
                                catch (Exception ex) { _host.LogIgnored("CleanupOnWindowClosed", ex); }
                                try { Restore(); } catch { }
                                try { _tcs.TrySetResult(PlaybackResult.Completed("Window closed", true)); } catch { }
                            };
                        }
                    }
                    catch (Exception ex) { _host.LogIgnored("HookWindowClosed", ex); }


                    _host._logger.LogInformation("UI thread: starting playback task");
                    ShowPlayerOverlay();
                    AttachViaSession(_m3u8Url);
                }
                catch (Exception ex)
                {
                    _host._logger.LogCritical(ex, "UI thread setup failed");
                    MainPage.SetNativePlayerActive(false);
                    _tcs.TrySetResult(PlaybackResult.Completed($"Player UI failed: {ex.Message}", true));
                }
            }
        }
    }
}
