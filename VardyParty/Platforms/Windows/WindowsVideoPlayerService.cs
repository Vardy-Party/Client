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
using System.Runtime.InteropServices;

namespace VardyParty.Platforms.Windows
{
    public class WindowsVideoPlayerService : INativeVideoPlayerService
    {
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

                static bool IsInteractiveSource(object? source)
                {
                    return source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                        || source is Microsoft.UI.Xaml.Controls.Slider
                        || source is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                        || source is Microsoft.UI.Xaml.Controls.ComboBox
                        || source is Microsoft.UI.Xaml.Controls.TextBox;
                }

                // Double-click on video surface toggles fullscreen/windowed.
                mediaPlayerElement.DoubleTapped += (_, e) =>
                {
                    try
                    {
                        if (IsInteractiveSource(e.OriginalSource)) return;

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

                // Click-drag on video surface moves the window (windowed mode only).
                // Use native OS caption drag for smooth movement.
                mediaPlayerElement.PointerPressed += (_, e) =>
                {
                    try
                    {
                        if (IsInteractiveSource(e.OriginalSource)) return;

                        var point = e.GetCurrentPoint(mediaPlayerElement);
                        if (!point.Properties.IsLeftButtonPressed) return;

                        var appWindow = nativeWindow.AppWindow;
                        if (appWindow == null) return;

                        if (appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                        {
                            return;
                        }

                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        if (hwnd != IntPtr.Zero)
                        {
                            ReleaseCapture();
                            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                            e.Handled = true;
                        }
                    }
                    catch { }
                };

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
                var scoresTickerRawText = string.Empty;
                double scoresTickerOffsetPx = 0;
                double tickerMeasuredTextWidth = 0;   // full double-copy text width
                double tickerLoopWidth = 0;            // width of one loop (single copy + separator)
                int tickerScrollDelayTicks = 0;
                bool tickerUserPaused = false;         // true while user is dragging or hovering after drag
                int tickerResumeCountdown = 0;         // counts down 60fps ticks after pointer leaves
                const int TickerReadDelayTicks = 180;  // ~3 seconds at 60fps before scrolling starts
                const int TickerResumeDelayTicks = 180; // ~3 seconds after pointer-exit before resuming
                const double tickerSpeedPerTickPx = 1.5;
                const string TickerSeparator = "   ⚽   "; // delimiter between loop copies
                Dictionary<string, List<Game>>? latestGamesByLeague = null;
                var gamesLock = new object();
                var scoresTickerMode = WindowsScoresTickerMode.SameLeagueInPlay;

                TypedEventHandler<MediaPlaybackSession, object>? playbackStateChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? naturalVideoSizeChangedHandler = null;
                TypedEventHandler<MediaPlaybackSession, object>? positionChangedHandler = null;
                TypedEventHandler<MediaPlayer, object>? mediaEndedHandler = null;
                TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailedHandler = null;
                
                bool metadataReported = false;
                bool isPointerInGrid = false;
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
                // Top-left hamburger button — true top-left anchor
                var menuButton = new WinButton
                {
                    Content = "☰",
                    HorizontalAlignment = WinHorizontalAlignment.Left,
                    VerticalAlignment = WinVerticalAlignment.Top,
                    Margin = new WinThickness(12, 12, 0, 0),
                    Width = 42,
                    Height = 42,
                    Opacity = 0,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
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
                // Canvas viewport: unlike Grid, Canvas does NOT constrain child width,
                // so the TextBlock lays out at its full natural width and the clip handles visibility.
                var scoresTickerViewport = new Microsoft.UI.Xaml.Controls.Canvas
                {
                    HorizontalAlignment = WinHorizontalAlignment.Stretch,
                    VerticalAlignment = WinVerticalAlignment.Stretch,
                    Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry()
                };
                WinGrid.SetColumn(scoresTickerViewport, 0);
                var scoresTickerText = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 15,
                    TextTrimming = Microsoft.UI.Xaml.TextTrimming.None,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
                    RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform()
                };
                // Position the text at the vertical centre of the canvas
                scoresTickerText.Loaded += (_, __) =>
                {
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(scoresTickerText,
                        (scoresTickerViewport.ActualHeight - scoresTickerText.ActualHeight) / 2);
                };
                scoresTickerViewport.SizeChanged += (_, __) =>
                {
                    // Keep clip rectangle in sync with viewport bounds
                    if (scoresTickerViewport.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rg)
                    {
                        rg.Rect = new global::Windows.Foundation.Rect(0, 0, scoresTickerViewport.ActualWidth, scoresTickerViewport.ActualHeight);
                    }
                    // Re-centre text vertically
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(scoresTickerText,
                        (scoresTickerViewport.ActualHeight - scoresTickerText.ActualHeight) / 2);
                };

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

                    if (scoresTickerText.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
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

                    if (scoresTickerText.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t)
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
                    FontSize = 15,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new WinThickness(0),
                    Padding = new WinThickness(8, 0, 8, 0),
                    VerticalAlignment = WinVerticalAlignment.Center
                };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tickerCycleButton, "Switch scores view");
                WinGrid.SetColumn(tickerCycleButton, 1);
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
                        .Where(g => g.IsFinished && g.HomeScore.HasValue && g.AwayScore.HasValue)
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

                            // Measure full double-copy width and single-loop width once per text update
                            if (tickerMeasuredTextWidth <= 0 || tickerLoopWidth <= 0)
                            {
                                scoresTickerText.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                                var fullWidth = scoresTickerText.DesiredSize.Width;
                                if (fullWidth <= 0) return;
                                tickerMeasuredTextWidth = fullWidth;
                                tickerLoopWidth = fullWidth / 2.0; // two identical copies
                            }

                            // Only scroll if a single copy is wider than the viewport
                            if (tickerLoopWidth <= viewportWidth)
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
                        };
                        scoresTickerScrollTimer.Tick += scoresTickerScrollHandler;
                    }
                }

                void RefreshTickerText(bool resetOffset)
                {
                    scoresTickerRawText = BuildCurrentModeTickerText();

                    // Build a double-copy for seamless continuous scrolling:
                    // "content ⚽ content ⚽ " — we scroll by exactly one loop width then reset
                    var single = scoresTickerRawText + TickerSeparator;
                    scoresTickerText.Text = single + single;

                    // Reset measured widths so they are re-measured on the next tick with new text
                    tickerMeasuredTextWidth = 0;
                    tickerLoopWidth = 0;

                    if (resetOffset)
                    {
                        scoresTickerOffsetPx = 0;
                        tickerScrollDelayTicks = 0;
                        tickerUserPaused = false;
                        tickerResumeCountdown = 0;
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

                // Next stream button host (button + x/y hint directly beneath)
                var nextButtonContainer = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
                    HorizontalAlignment = WinHorizontalAlignment.Right,
                    VerticalAlignment = WinVerticalAlignment.Center,
                    Margin = new WinThickness(0, 0, 48, 0),
                    Spacing = 4,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    Opacity = 0
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

                var grid = new WinGrid();
                grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
                // Don't add mediaPlayerElement here yet - defer until source is initialized
                grid.Children.Add(dismissSurface);
                grid.Children.Add(infoPanel);
                grid.Children.Add(scoresTickerBorder);
                grid.Children.Add(streamInfoPanel);
                grid.Children.Add(menuPanel);
                grid.Children.Add(menuButton);
                grid.Children.Add(nextButtonContainer);

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
                    grid.PointerEntered += (s, e) =>
                    {
                        isPointerInGrid = true;
                        if (nextButtonContainer.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                            nextButtonContainer.Opacity = 1;
                    };
                    grid.PointerExited  += (s, e) =>
                    {
                        isPointerInGrid = false;
                        nextButtonContainer.Opacity = 0;
                        nextButton.Background = nextBgNormal;
                        nextButtonHintText.Opacity = 0;
                    };
                    nextButton.PointerEntered += (s, e) =>
                    {
                        nextButton.Background = nextBgHover;
                        if (nextButtonContainer.Visibility == Microsoft.UI.Xaml.Visibility.Visible && !string.IsNullOrWhiteSpace(nextButtonHintText.Text))
                        {
                            nextButtonHintText.Opacity = 1;
                        }
                    };
                    nextButton.PointerExited  += (s, e) =>
                    {
                        nextButton.Background = nextBgNormal;
                        nextButtonHintText.Opacity = 0;
                    };
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
                            nextButtonContainer.Visibility = canSwitchToAnother
                                ? Microsoft.UI.Xaml.Visibility.Visible
                                : Microsoft.UI.Xaml.Visibility.Collapsed;
                            if (!canSwitchToAnother)
                            {
                                nextButtonContainer.Opacity = 0;
                                nextButtonHintText.Text = string.Empty;
                                nextButtonHintText.Opacity = 0;
                            }
                            else
                            {
                                nextButtonHintText.Text = $"{index}/{total}";
                                // If the cursor is already over the player when another stream arrives,
                                // show the Next button immediately without requiring pointer re-enter.
                                nextButtonContainer.Opacity = isPointerInGrid ? 1 : 0;
                            }

                            var shouldShowStreamOverlay = total > 0 && (hasChanged || hasResolutionChanged);
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

                            // Guard: only cancel if our video grid is still the active content.
                            // If Restore() already ran, content has been swapped — let the close go through.
                            if (!ReferenceEquals(nativeWindow.Content, grid))
                                return;

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
