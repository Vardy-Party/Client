using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Linux.Services;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.Streaming;

namespace VardyParty.Linux.Pages;

/// <summary>
/// Linux-head host for the shared XAML homepage: the same auth +
/// stream-resolution glue as the MAUI head's HomeHostPage, with two
/// Linux-specific twists — sign-in uses the Auth0 device-code flow with a QR
/// code, and playback composites in-window (or libvlc's own window as
/// fallback) with chrome overlaid on the picture (see
/// <see cref="LinuxVideoPlayerService"/>).
/// Set VARDYPARTY_LINUX_SAMPLE_DATA=1 to skip auth and render a fabricated
/// catalog (demos and the headless CI smoke test).
/// </summary>
public partial class LinuxHomePage : ContentPage
{
    private readonly ILogger<LinuxHomePage> _logger;
    private readonly HomeViewModel _viewModel;
    private readonly IEnrichedGameService _gameService;
    private readonly IStreamResolutionOrchestrator _orchestrator;
    private readonly INativeVideoPlayerService _videoPlayer;
    private readonly IStreamSwitchingService _switching;
    private readonly IAuthTokenProvider _authTokens;
    private readonly IAuthLoginService _authLogin;
    private readonly ILocalLanServiceAvailabilityMonitor _lanMonitor;
    private readonly SelectionState _selection;
    private readonly UiSoundService _sounds;
    private readonly MatchEventNotificationPolicy _notifications;
    private readonly IUiSoundPlayer _soundPlayer;
    private readonly MatchEventBus _matchEvents;
    private readonly Auth0Settings _auth0Settings;
    private readonly HomeShellViewModel _homeShell = new();
    private readonly MatchEventToastViewModel _playbackToast;

    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _progressSubscription;
    private readonly List<IDisposable> _chromeSubscriptions = new();
    private bool _initialized;
    private bool _isAuthenticated;
    private bool _isAuthenticating;
    private CancellationTokenSource? _authCts;
    private AuthDeviceCode? _deviceCode;

    private string? _serviceError;
    private string? _lanWarning;

    private PlaybackChromePresenter? _playbackChrome;
    private List<Game> _gamesSnapshot = new();
    private bool _chromeVisible;
    private readonly LinuxPlaybackFullscreenSession _fullscreenSession = new();

    // Stream resolution state (mirrors HomeHostPage's fields).
    private bool _isResolvingStreams;
    /// <summary>
    /// True from overlay show until we explicitly hide it. Must NOT track
    /// <see cref="StreamResolutionProgress.IsResolving"/>: the orchestrator's
    /// BehaviorSubject emits an initial IsResolving=false on first subscribe
    /// (and Reset can emit the same), which hid the finding-streams modal on
    /// first pick before later progress arrived.
    /// </summary>
    private bool _resolveOverlayOpen;
    private bool _resolutionStartClaimed;
    private bool _resolutionExhausted;
    private int _resolutionGeneration;
    private CancellationTokenSource? _resolutionCts;
    private Task? _resolutionTask;

    private bool _escapeWired;
    private Avalonia.Controls.TopLevel? _playbackTopLevel;
    private readonly LinuxCloseChipReveal _closeChip = new();
    private IDispatcherTimer? _closeChipHideTimer;

    private static bool UseSampleData =>
        Environment.GetEnvironmentVariable("VARDYPARTY_LINUX_SAMPLE_DATA") == "1";

    public LinuxHomePage(
        ILogger<LinuxHomePage> logger,
        HomeViewModel viewModel,
        IEnrichedGameService gameService,
        IStreamResolutionOrchestrator orchestrator,
        INativeVideoPlayerService videoPlayer,
        IStreamSwitchingService switching,
        IAuthTokenProvider authTokens,
        IAuthLoginService authLogin,
        ILocalLanServiceAvailabilityMonitor lanMonitor,
        SelectionState selection,
        UiSoundService sounds,
        MatchEventNotificationPolicy notifications,
        IUiSoundPlayer soundPlayer,
        MatchEventBus matchEvents,
        IOptions<Auth0Settings> auth0Settings)
    {
        _logger = logger;
        _viewModel = viewModel;
        _gameService = gameService;
        _orchestrator = orchestrator;
        _videoPlayer = videoPlayer;
        _switching = switching;
        _authTokens = authTokens;
        _authLogin = authLogin;
        _lanMonitor = lanMonitor;
        _selection = selection;
        _sounds = sounds;
        _notifications = notifications;
        _soundPlayer = soundPlayer;
        _matchEvents = matchEvents;
        _auth0Settings = auth0Settings.Value;

        InitializeComponent();
        BindingContext = _viewModel;

        // In-playback match-event toast: stacked over the composited picture
        // next to Close. Same queue/dismiss machine as the homepage toast.
        // Audio stays suppressed during playback via ShouldPlayAudio —
        // toast-yes/audio-no.
        _playbackToast = new MatchEventToastViewModel(_viewModel.Layout);
        PlaybackToast.BindingContext = _playbackToast;
        _playbackToast.PropertyChanged += OnPlaybackToastPropertyChanged;
        _matchEvents.Published += OnMatchEventPublished;
        _closeChip.OverlayOnVideo = true;
        WireCloseChipGestures();
        WirePlayerChrome();

        _viewModel.GamePicked += OnGamePicked;
        _viewModel.SignOutRequested += () => _ = SignOutAsync();

        // Yield the UI-sound device while video is up; recover it on Close.
        _videoPlayer.PlaybackVisibilityChanged += OnPlaybackVisibilityChanged;

#if EMBEDDED_LINUX_VIDEO
        WireEmbeddedVideoHost();
#endif
    }

#if EMBEDDED_LINUX_VIDEO
    private Controls.VideoHostView? _videoHost;

    /// <summary>
    /// Hosted-surface wiring for in-window compositing: the service asks this
    /// page (via <see cref="LinuxVideoPlayerService.AcquireFramePresenterAsync"/>)
    /// for the VideoHostView presenter BEFORE Play, then attaches software
    /// callbacks. Clean stop clears the frame; a wedged pair parks the host.
    /// </summary>
    private void WireEmbeddedVideoHost()
    {
        if (_videoPlayer is not LinuxVideoPlayerService service)
        {
            return;
        }

        service.AcquireFramePresenterAsync = AcquireFramePresenterOnUiAsync;
        service.DetachSurfaceRequested += OnDetachSurfaceRequested;
        service.SurfacePoisoned += OnSurfacePoisoned;
        service.EmbeddingStateChanged += OnEmbeddingStateChanged;
    }

    /// <summary>
    /// UI-thread half of the embed handshake: show a (fresh or reused)
    /// VideoHostView and return it as the frame presenter. The service
    /// attaches LibVLC callbacks on a worker; this method must never block
    /// its caller (always dispatches) and never calls into libvlc.
    /// </summary>
    private Task<IVideoFramePresenter?> AcquireFramePresenterOnUiAsync()
    {
        var tcs = new TaskCompletionSource<IVideoFramePresenter?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Dispatch(() =>
        {
            try
            {
                if (_videoHost == null)
                {
                    _videoHost = new Controls.VideoHostView();
                    VideoHostContainer.Children.Add(_videoHost);
                }

                VideoHostContainer.IsVisible = true;
                StandalonePlaybackPanel.IsVisible = false;
                tcs.TrySetResult(_videoHost);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Clean stop: drop the last composited frame (player is idle).</summary>
    private void OnDetachSurfaceRequested() => Dispatcher.Dispatch(() =>
    {
        _videoHost?.ClearFrame();
    });

    /// <summary>
    /// A libvlc pair was abandoned as wedged: park the current host and
    /// forget it. The next session builds a fresh presenter so in-flight
    /// frames cannot land on a poisoned generation.
    /// </summary>
    private void OnSurfacePoisoned() => Dispatcher.Dispatch(() =>
    {
        if (_videoHost is { } poisoned)
        {
            poisoned.IsVisible = false;
            _videoHost = null;
        }

        VideoHostContainer.IsVisible = false;
        StandalonePlaybackPanel.IsVisible = true;
    });

    private void OnEmbeddingStateChanged(object? sender, bool embedded) => Dispatcher.Dispatch(() =>
    {
        VideoHostContainer.IsVisible = embedded;
        StandalonePlaybackPanel.IsVisible = !embedded;
    });
#endif

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        TryWireEscapeClose();
    }

    /// <summary>
    /// Escape is the same cancel path as the Close chip. Wired on the
    /// Avalonia TopLevel (tunnel) so it still fires when focus is in the
    /// homepage under the overlay. MAUI Button.KeyboardAccelerators is not
    /// mapped on this Avalonia backend (MAUIX2002).
    /// </summary>
    private void TryWireEscapeClose()
    {
        if (_escapeWired)
        {
            return;
        }

        try
        {
            if (Handler?.PlatformView is not Avalonia.Visual visual)
            {
                return;
            }

            var top = Avalonia.Controls.TopLevel.GetTopLevel(visual);
            if (top is null)
            {
                return;
            }

            _playbackTopLevel = top;
            top.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
            top.AddHandler(InputElement.PointerMovedEvent, OnTopLevelPointerMoved, RoutingStrategies.Tunnel);
            top.AddHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
            _escapeWired = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] Escape-to-close wiring skipped");
        }
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!PlaybackOverlay.IsVisible)
        {
            return;
        }

        if (e.Key == Key.F11)
        {
            TogglePlaybackFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_playbackChrome?.TryDismissLayer() == true)
            {
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;
            }

            HandleEscapeBeyondChrome();
            e.Handled = true;
            return;
        }

        HandlePlaybackChromeKey(e);
    }

    private void OnTopLevelPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!PlaybackOverlay.IsVisible || _playbackTopLevel is not { } top)
        {
            return;
        }

        var pos = e.GetPosition(top);
        if (LinuxCloseChipReveal.IsNearRestingPlace(
                pos.X, pos.Y, top.Bounds.Width, _closeChip.IsRevealed, _closeChip.OverlayOnVideo))
        {
            ApplyCloseChip(_closeChip.OnHoverEnter());
        }
        else if (_closeChip.Hovering)
        {
            ApplyCloseChip(_closeChip.OnHoverLeave());
        }
    }

    private void OnTopLevelPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!PlaybackOverlay.IsVisible)
        {
            return;
        }

        // Presses on the composited picture and overlay chrome reach Avalonia.
        // Does not close; the chip click does.
        ApplyCloseChip(_closeChip.OnTouched());

        // Double-click on the video surface toggles host fullscreen (same as F11 /
        // menu). Skip when chrome layers are open so dismiss taps are not toggles.
        if (e.ClickCount == 2
            && _playbackChrome?.IsMenuVisible != true
            && _playbackChrome?.IsVideoInfoVisible != true)
        {
            TogglePlaybackFullscreen();
            e.Handled = true;
        }
    }

    private void WireCloseChipGestures()
    {
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => ApplyCloseChip(_closeChip.OnHoverEnter());
        hover.PointerExited += (_, _) => ApplyCloseChip(_closeChip.OnHoverLeave());
        CloseHitZone.GestureRecognizers.Add(hover);

        var stripTap = new TapGestureRecognizer();
        stripTap.Tapped += (_, _) => ApplyCloseChip(_closeChip.OnTouched());
        PlaybackChromeRow.GestureRecognizers.Add(stripTap);

        var standaloneTap = new TapGestureRecognizer();
        standaloneTap.Tapped += (_, _) => ApplyCloseChip(_closeChip.OnTouched());
        StandalonePlaybackPanel.GestureRecognizers.Add(standaloneTap);

#if EMBEDDED_LINUX_VIDEO
        var videoTap = new TapGestureRecognizer();
        videoTap.Tapped += (_, _) => ApplyCloseChip(_closeChip.OnTouched());
        VideoHostContainer.GestureRecognizers.Add(videoTap);
#endif
    }

    private void OnPlaybackToastPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MatchEventToastViewModel.IsToastVisible))
        {
            ApplyCloseChipVisuals();
        }
    }

    private void ApplyCloseChip(LinuxCloseChipAction action)
    {
        if (_chromeVisible)
            ApplyPlayerChromeVisuals();
        else
            ApplyCloseChipVisuals();

        switch (action)
        {
            case LinuxCloseChipAction.StartAutoHide:
                ArmCloseChipHideTimer();
                break;
            case LinuxCloseChipAction.CancelAutoHide:
                _closeChipHideTimer?.Stop();
                break;
        }
    }

    private void ApplyCloseChipVisuals()
    {
        var revealed = _closeChip.ChipVisible;
        ClosePlaybackButton.Opacity = revealed ? 1 : 0;
        ClosePlaybackButton.InputTransparent = !revealed;
        ClosePlaybackButton.IsEnabled = revealed;

        var height = _closeChip.ReserveHeight(_playbackToast.IsToastVisible);
        if (double.IsNaN(height))
        {
            PlaybackChromeRow.HeightRequest = -1;
            PlaybackChromeRow.MinimumHeightRequest = 0;
        }
        else
        {
            PlaybackChromeRow.HeightRequest = height;
            PlaybackChromeRow.MinimumHeightRequest = height;
        }

        CloseHitZone.HeightRequest = _closeChip.HitZoneHeight;
        CloseHitZone.WidthRequest = LinuxCloseChipReveal.HitZoneWidth;
    }

    private void ArmCloseChipHideTimer()
    {
        _closeChipHideTimer ??= CreateCloseChipHideTimer();
        _closeChipHideTimer.Stop();
        _closeChipHideTimer.Start();
    }

    private IDispatcherTimer CreateCloseChipHideTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = LinuxCloseChipReveal.AutoHideDelay;
        timer.Tick += (_, _) =>
        {
            _closeChipHideTimer?.Stop();
            ApplyCloseChip(_closeChip.OnAutoHideElapsed());
        };
        return timer;
    }

    private void ResetCloseChip()
    {
        _closeChipHideTimer?.Stop();
        _closeChip.Reset();
        ApplyCloseChipVisuals();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        TryWireEscapeClose();
        if (_initialized) return;
        _initialized = true;

        // Preload UI sounds on a background task after first render — never in
        // the startup path. Headless machines log-and-degrade to silence.
        _ = Task.Run(() => _soundPlayer.InitializeAsync());

        _subscriptions.Add(_lanMonitor.WarningStream.Subscribe(warning =>
        {
            _lanWarning = warning;
            _viewModel.SetLanWarning(warning);
        }));

        if (UseSampleData)
        {
            _logger.LogInformation("[LinuxHome] Sample data mode: skipping auth");
            _viewModel.UpdateGames(SampleGames.Build());

            // Exercise the in-place diff path (goal, minute ticks, add/remove,
            // live-set re-tier) on the real UI a few seconds in — the headless
            // CI smoke keeps the app alive for ~20s, so a crash in the refresh
            // path fails the gate instead of shipping.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                _logger.LogInformation("[LinuxHome] Sample data mode: applying refreshed board");
                _viewModel.UpdateGames(SampleGames.BuildRefreshed());
            });
            return;
        }

        _ = InitializeAuthAsync();
    }

    private async Task InitializeAuthAsync()
    {
        _logger.LogInformation("[LinuxHome] Initialize start");
        _isAuthenticated = await _authTokens.IsAuthenticatedAsync();
        _viewModel.CanSignOut = _isAuthenticated;

        if (_isAuthenticated)
        {
            StartGamesFeed();
            SetAuthOverlayVisible(false);
        }
        else
        {
            SetAuthOverlayVisible(true);
        }

        _logger.LogInformation("[LinuxHome] Initialize complete (authenticated={Authenticated})", _isAuthenticated);
    }

    private void StartGamesFeed()
    {
        _subscriptions.Add(_gameService.GamesStream.Subscribe(dict => _viewModel.UpdateGames(dict)));
        _subscriptions.Add(_gameService.ErrorStream.Subscribe(error =>
        {
            _serviceError = error;
            _viewModel.SetServiceError(error);
        }));

        (_gameService as EnrichedGameService)?.StartBackgroundPolling();
    }

    /// <summary>Service errors outrank the LAN warning on the shared banner.</summary>
    private void PushErrorBanner()
    {
        _viewModel.SetServiceError(_serviceError);
        _viewModel.SetLanWarning(_lanWarning);
    }

    // ---------------------------------------------------------------- auth --

    private async void OnSignInClicked(object? sender, EventArgs e) => await StartSignInAsync();

    private void OnCancelSignInClicked(object? sender, EventArgs e) => CancelSignIn();

    private async Task StartSignInAsync()
    {
        if (_isAuthenticating) return;

        _logger.LogInformation("[LinuxHome] Sign in pressed");
        _isAuthenticating = true;
        _deviceCode = null;
        _authCts?.Cancel();
        _authCts = new CancellationTokenSource();
        Dispatcher.Dispatch(() =>
        {
            SignInButton.IsEnabled = false;
            SignInButton.Text = "Signing in…";
            SetAuthStatus(null);
        });

        try
        {
            // Prefer system-browser PKCE (loopback) whenever a browser can open —
            // including when shared secrets still use the MAUI vardyparty:// scheme.
            // Device-code + QR is the fallback for headless / no-browser hosts.
            ShowBrowserSignInWaiting();
            var interactive = await _authLogin.LoginInteractiveAsync(_authCts.Token);
            if (interactive.IsSuccess && !string.IsNullOrWhiteSpace(interactive.AccessToken))
            {
                OnSignedIn();
                return;
            }

            if (!string.Equals(interactive.Error, LinuxAuthService.BrowserUnavailableError, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(interactive.Error))
                    SetAuthStatus(interactive.Error);
                return;
            }

            _logger.LogInformation("[LinuxHome] Browser unavailable; falling back to device-code sign-in");
            SetAuthStatus("No browser available — use the code below on another device.");

            var deviceLogin = await _authLogin.StartDeviceLoginAsync(_authCts.Token);
            if (deviceLogin == null)
            {
                SetAuthStatus(DescribeDeviceSignInUnavailable());
                return;
            }

            _deviceCode = deviceLogin.DeviceCode;
            ShowDeviceCode(_deviceCode);

            var result = await _authLogin.PollDeviceLoginAsync(deviceLogin.DeviceCode, _authCts.Token);
            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.AccessToken))
            {
                OnSignedIn();
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SetAuthStatus(result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            SetAuthStatus("Sign-in canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Sign-in failed");
            SetAuthStatus(string.IsNullOrWhiteSpace(ex.Message) ? "Sign-in failed." : ex.Message);
        }
        finally
        {
            _isAuthenticating = false;
            _deviceCode = null;
            Dispatcher.Dispatch(() =>
            {
                DeviceCodePanel.IsVisible = false;
                SignInButton.IsEnabled = true;
                SignInButton.Text = "Sign in — Continue";
            });
        }
    }

    private void ShowBrowserSignInWaiting()
    {
        Dispatcher.Dispatch(() =>
        {
            DeviceCodeLabel.Text = "Complete sign-in in your browser";
            DeviceUriLabel.Text = "A browser window should open. Use Cancel to abort.";
            DeviceQrImage.Source = null;
            DeviceQrImage.IsVisible = false;
            DeviceCodePanel.IsVisible = true;
            SetAuthStatus("Opening browser for sign-in…");
        });
    }

    private string DescribeDeviceSignInUnavailable()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_auth0Settings.Domain))
            missing.Add("Domain");
        if (string.IsNullOrWhiteSpace(_auth0Settings.ClientId))
            missing.Add("ClientId");

        if (missing.Count > 0)
        {
            return
                $"Sign-in unavailable: Auth0 {string.Join(" and ", missing)} empty in this build. " +
                "Relaunch with secrets merged (scripts/launch-linux-app.ps1 or -p:PatchAppSettings=true).";
        }

        return "Unable to start device sign-in. Check Auth0 configuration and network, then try again.";
    }

    private void OnSignedIn()
    {
        _isAuthenticated = true;
        _viewModel.CanSignOut = true;
        SetAuthStatus(null);
        StartGamesFeed();
        SetAuthOverlayVisible(false);
    }

    private void CancelSignIn()
    {
        _authCts?.Cancel();
        _deviceCode = null;
        _isAuthenticating = false;
        SetAuthStatus("Sign-in canceled.");
        Dispatcher.Dispatch(() =>
        {
            DeviceCodePanel.IsVisible = false;
            SignInButton.IsEnabled = true;
            SignInButton.Text = "Sign in — Continue";
            SignInButton.Focus();
        });
    }

    private async Task SignOutAsync()
    {
        _logger.LogInformation("[LinuxHome] Signing out");
        try
        {
            _authCts?.Cancel();
        }
        catch
        {
        }

        _authCts = null;
        _isAuthenticating = false;
        _deviceCode = null;

        await _authTokens.LogoutAsync();

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _progressSubscription?.Dispose();
        _progressSubscription = null;
        _orchestrator.Reset();

        _serviceError = null;
        _lanWarning = null;
        _selection.CurrentGame = null;
        _homeShell.ClearSelection();
        _isAuthenticated = false;

        Dispatcher.Dispatch(() =>
        {
            _viewModel.CanSignOut = false;
            _viewModel.CloseMenu();
            _viewModel.UpdateGames(null);
            _viewModel.ClearErrors();
            _viewModel.ResetScoreObservations();
            SetAuthOverlayVisible(true);
        });

        // The LAN warning stream keeps running across sign-in sessions.
        _subscriptions.Add(_lanMonitor.WarningStream.Subscribe(warning =>
        {
            _lanWarning = warning;
            _viewModel.SetLanWarning(warning);
        }));
    }

    private void ShowDeviceCode(AuthDeviceCode deviceCode)
    {
        var target = string.IsNullOrWhiteSpace(deviceCode.VerificationUriComplete)
            ? deviceCode.VerificationUri
            : deviceCode.VerificationUriComplete;

        byte[]? qrPng = null;
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(target ?? string.Empty, QRCodeGenerator.ECCLevel.Q);
            qrPng = new PngByteQRCode(qrData).GetGraphic(20);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Failed to generate QR code locally");
        }

        Dispatcher.Dispatch(() =>
        {
            DeviceCodeLabel.Text = $"Code {deviceCode.UserCode}";
            DeviceUriLabel.Text = $"Scan the QR code, or open {target}";
            DeviceQrImage.Source = qrPng == null
                ? null
                : ImageSource.FromStream(() => new MemoryStream(qrPng));
            DeviceQrImage.IsVisible = qrPng != null;
            DeviceCodePanel.IsVisible = true;
            CancelSignInButton.Focus();
        });
    }

    private void SetAuthOverlayVisible(bool visible) => Dispatcher.Dispatch(() =>
    {
        AuthOverlay.IsVisible = visible;
        if (visible)
        {
            SignInButton.Focus();
        }
    });

    private void SetAuthStatus(string? message) => Dispatcher.Dispatch(() =>
    {
        AuthStatusLabel.Text = message ?? string.Empty;
        AuthStatusLabel.IsVisible = !string.IsNullOrWhiteSpace(message);
    });

    // --------------------------------------------------- stream resolution --

    private void OnGamePicked(Game game)
    {
        if (TestMediaPath is { } testMedia)
        {
            _ = PlayTestMediaAsync(game, testMedia);
            return;
        }

        _ = StartStreamResolutionAsync(game);
    }

    /// <summary>
    /// TEST-ONLY hook (headless verification of the in-window playback path):
    /// VARDYPARTY_LINUX_TEST_MEDIA=&lt;path-or-url&gt; makes a card pick play
    /// that media directly through the real player service instead of
    /// resolving streams. Never set in production; pairs with
    /// VARDYPARTY_LINUX_SAMPLE_DATA=1 for the xvfb evidence runs.
    /// </summary>
    private static string? TestMediaPath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("VARDYPARTY_LINUX_TEST_MEDIA");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    private async Task PlayTestMediaAsync(Game game, string testMedia)
    {
        // Local paths become file:// URIs; anything with a scheme (including
        // a deliberately stalling http endpoint for the Close-responsiveness
        // proof) passes through untouched.
        var mediaUrl = testMedia.Contains("://", StringComparison.Ordinal)
            ? testMedia
            : new Uri(Path.GetFullPath(testMedia)).AbsoluteUri;
        var title = $"{game.DisplayHome} v {game.DisplayAway}";
        _logger.LogInformation(
            "[LinuxHome] TEST MEDIA hook: playing {Url} for '{Title}' (stream resolution bypassed)",
            mediaUrl, title);
        try
        {
            var result = await _videoPlayer.PlayVideoAsync(
                mediaUrl, refererUrl: string.Empty, title,
                league: game.League, homeTeam: game.DisplayHome, awayTeam: game.DisplayAway);
            _logger.LogInformation(
                "[LinuxHome] TEST MEDIA playback ended (success={Success}, message={Message})",
                result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] TEST MEDIA playback failed");
        }
        finally
        {
            _viewModel.OnStreamResolutionEnded();
        }
    }

    private async Task StartStreamResolutionAsync(Game game)
    {
        _logger.LogInformation(
            "[LinuxHome] Starting stream resolution for {Home} vs {Away}", game.DisplayHome, game.DisplayAway);

        if (_resolutionStartClaimed || _resolutionTask is { IsCompleted: false })
        {
            var sameGame = _homeShell.SelectedGame != null
                && HomePlaybackIntent.SameGame(_homeShell.SelectedGame, game);
            if (HomePlaybackIntent.ShouldIgnoreRepick(sameGame, _resolutionExhausted))
            {
                _logger.LogInformation("[LinuxHome] Stream resolution already running for this game");
                return;
            }

            _resolutionCts?.Cancel();
        }

        var generation = Interlocked.Increment(ref _resolutionGeneration);
        _resolutionStartClaimed = true;

        _homeShell.OnUserPicked(game);
        _selection.CurrentGame = game;
        _isResolvingStreams = true;
        _resolveOverlayOpen = true;
        _resolutionExhausted = false;
        ShowResolveOverlay($"{game.DisplayHome} v {game.DisplayAway}");

        _progressSubscription ??= _orchestrator.ProgressUpdated.Subscribe(progress =>
        {
            if (progress.HealthyStreams > 0)
            {
                _homeShell.MarkPlayerSessionStarted();
            }

            // Drive chrome/title from progress, but never clear
            // _resolveOverlayOpen here — see field comment.
            _isResolvingStreams = progress.IsResolving || _resolveOverlayOpen;
            UpdateResolveOverlay(progress);
        });

        if (generation != Volatile.Read(ref _resolutionGeneration))
        {
            return;
        }

        _resolutionCts?.Cancel();
        _resolutionCts = new CancellationTokenSource();
        _resolutionTask = Task.Run(async () =>
        {
            try
            {
                var outcome = await _orchestrator.StartAsync(game, _videoPlayer, _resolutionCts.Token);
                if (generation != Volatile.Read(ref _resolutionGeneration))
                {
                    return;
                }

                _resolutionExhausted = true;

                var plan = StreamResolutionOutcomeUx.Plan(outcome);
                if (plan.ClearSelection)
                {
                    _selection.CurrentGame = null;
                    _homeShell.ClearSelection();
                }

                if (plan.ErrorMessage != null)
                {
                    ShowStreamPlaybackError(plan.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[LinuxHome] Stream resolution cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LinuxHome] Error during stream resolution");
                if (generation != Volatile.Read(ref _resolutionGeneration))
                {
                    return;
                }

                _resolutionExhausted = true;
                _selection.CurrentGame = null;
                _homeShell.ClearSelection();
                ShowStreamPlaybackError(StreamResolutionOutcomeUx.PlanException(ex.Message).ErrorMessage);
            }
            finally
            {
                if (generation == Volatile.Read(ref _resolutionGeneration))
                {
                    _resolutionStartClaimed = false;
                    _isResolvingStreams = false;
                    _resolveOverlayOpen = false;
                    _viewModel.OnStreamResolutionEnded();
                    Dispatcher.Dispatch(() =>
                    {
                        ResolveOverlay.IsVisible = false;
                        TryResumeAfterPlayer();
                    });
                }
            }
        });

        await Task.CompletedTask;
    }

    private void CancelStreamDiscoveryFromUser()
    {
        try
        {
            _resolutionCts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] Cancel of resolution CTS failed");
        }

        try
        {
            _orchestrator.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] Orchestrator reset on cancel failed");
        }

        _progressSubscription?.Dispose();
        _progressSubscription = null;
        Interlocked.Increment(ref _resolutionGeneration);
        _resolutionStartClaimed = false;
        _resolutionExhausted = true;
        _isResolvingStreams = false;
        _resolveOverlayOpen = false;
        _selection.CurrentGame = null;
        _homeShell.ClearSelection();
        _viewModel.OnStreamResolutionEnded();
        _sounds.Play(UiSound.Back);
        _logger.LogInformation("[LinuxHome] Stream discovery cancelled by user");

        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }

    private void OnResolveCancelClicked(object? sender, EventArgs e) => CancelStreamDiscoveryFromUser();

    /// <summary>
    /// Escape / Android-back while the playback overlay is up dismisses chrome
    /// layers first, then closes playback (same as the Close chip).
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (PlaybackOverlay.IsVisible)
        {
            if (_playbackChrome?.TryDismissLayer() == true)
            {
                ApplyPlayerChromeVisuals();
                return true;
            }

            HandleEscapeBeyondChrome();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    /// <summary>Close chip for in-window / standalone libvlc playback.</summary>
    private void OnClosePlaybackClicked(object? sender, EventArgs e)
    {
        // Prefer presenter Exit so layers/toast clear; ExitRequested then
        // completes StopPlayback. If chrome was never created, stop directly.
        if (_playbackChrome is not null)
        {
            try
            {
                _playbackChrome.Exit();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[LinuxHome] Chrome Exit during close failed");
            }
        }

        CompletePlaybackClose();
    }

    private void CompletePlaybackClose()
    {
        try
        {
            _resolutionCts?.Cancel();
            (_videoPlayer as LinuxVideoPlayerService)?.StopPlayback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Failed to close playback");
        }

        Dispatcher.Dispatch(() =>
        {
            ExitPlaybackFullscreenIfNeeded();
            HidePlaybackChrome();
            ResetCloseChip();
            PlaybackOverlay.IsVisible = false;
        });
    }

    /// <summary>
    /// Bus callbacks arrive on the UI thread (the catalog apply pump). Only
    /// the playback surface consumes here — the homepage toast has its own
    /// subscriber inside HomeViewModel and shows when the panel is down.
    /// </summary>
    private void OnMatchEventPublished(MatchEvent matchEvent)
    {
        if (!_notifications.IsPlaybackActive)
        {
            return;
        }

        _playbackToast.Publish(_viewModel.BuildToastItem(matchEvent));
    }

    private void OnPlaybackVisibilityChanged(object? sender, bool visible)
    {
        // Suppress + yield the miniaudio device before libvlc Play; un-suppress
        // + recover it after Close / a failed session (see PlaybackAudioSession).
        PlaybackAudioSession.Apply(visible, _sounds, _soundPlayer);

        // Homepage stays visible next to the native VLC window, but it is no
        // longer the active surface: match events downgrade to toast-only.
        _notifications.IsPlaybackActive = visible;
        Dispatcher.Dispatch(() =>
        {
            PlaybackOverlay.IsVisible = visible;
            if (visible)
            {
                ResetCloseChip();
                ShowPlaybackChrome();
            }
            else
            {
                HidePlaybackChrome();
                ResetPlayerChrome();
                ResetCloseChip();
                TryResumeAfterPlayer();
            }
        });
    }

    private PlaybackChromePresenter EnsurePlaybackChrome()
    {
        if (_playbackChrome is not null)
            return _playbackChrome;

        _playbackChrome = new PlaybackChromePresenter(
            reportBadStream: async (reason, _) =>
                await _orchestrator.ReportCurrentStreamAsBadAsync(reason),
            requestNext: () =>
            {
                if (_videoPlayer is LinuxVideoPlayerService linux)
                    return linux.RequestNextStreamAsync();
                return Task.CompletedTask;
            },
            requestPrevious: () =>
            {
                if (_videoPlayer is LinuxVideoPlayerService linux)
                {
                    linux.RequestPreviousStream();
                }

                return Task.CompletedTask;
            },
            cleanupPool: () =>
            {
                try { _switching.Cleanup(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LinuxHome] Switching cleanup failed");
                }
            });

        _playbackChrome.ExitRequested += (_, _) =>
            Dispatcher.Dispatch(CompletePlaybackClose);
        _playbackChrome.StateChanged += (_, _) =>
            Dispatcher.Dispatch(ApplyPlayerChromeVisuals);
        _playbackChrome.StreamToastRequested += (_, _) =>
            Dispatcher.Dispatch(() => ApplyStreamToastTimer(start: true));
        _playbackChrome.StreamToastDismissed += (_, _) =>
            Dispatcher.Dispatch(() => ApplyStreamToastTimer(start: false));

        return _playbackChrome;
    }

    private void ShowPlaybackChrome()
    {
        try
        {
            EnsurePlaybackChrome();
            WirePlayerChrome();
            _chromeVisible = true;
            PushOverlayInfoFromSwitching();
            ApplyPlayerChromeVisuals();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Failed to show playback chrome");
        }
    }

    private void HidePlaybackChrome()
    {
        _chromeVisible = false;
    }

    private void PushOverlayInfoFromSwitching()
    {
        try
        {
            var current = _switching.GetCurrentStream();
            var total = _switching.GetHealthyStreams().Count;
            var index = _switching.GetCurrentStreamIndex();
            var info = PlayerOverlayFormatter.BuildOverlayInfo(
                current, index, total, current?.Referer);
            ApplyOverlayInfo(info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] PushOverlayInfoFromSwitching failed");
        }
    }

    private void ApplyOverlayInfo(PlayerOverlayInfo? info)
    {
        if (_playbackChrome is null)
            return;

        _playbackChrome.ApplyOverlayInfo(info);
        if (info is not null)
            _playbackChrome.NotifyHealthyCount(info.Total);
    }

    /// <summary>
    /// Escape after chrome layers are gone: exit host fullscreen first, then close.
    /// </summary>
    private void HandleEscapeBeyondChrome()
    {
        switch (LinuxPlaybackEscapeOrder.Next(_fullscreenSession.IsFullscreen))
        {
            case LinuxPlaybackEscapeAction.ExitFullscreen:
                ExitPlaybackFullscreenIfNeeded();
                break;
            default:
                OnClosePlaybackClicked(this, EventArgs.Empty);
                break;
        }
    }

    private void TogglePlaybackFullscreen()
    {
        if (_playbackTopLevel is not Avalonia.Controls.Window window)
        {
            _logger.LogDebug("[LinuxHome] Fullscreen toggle skipped — no Avalonia Window");
            return;
        }

        try
        {
            var current = ToHostWindowMode(window.WindowState);
            var next = _fullscreenSession.Toggle(current);
            window.WindowState = ToAvaloniaWindowState(next);
            ApplyPlayerChromeVisuals();
            _logger.LogInformation(
                "[LinuxHome] Playback host window -> {State} (fullscreenSession={IsFs})",
                next, _fullscreenSession.IsFullscreen);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] TogglePlaybackFullscreen failed");
        }
    }

    private void ExitPlaybackFullscreenIfNeeded()
    {
        if (!_fullscreenSession.IsFullscreen)
            return;

        if (_playbackTopLevel is not Avalonia.Controls.Window window)
        {
            _fullscreenSession.Reset();
            ApplyPlayerChromeVisuals();
            return;
        }

        try
        {
            var restore = _fullscreenSession.Exit();
            window.WindowState = ToAvaloniaWindowState(restore);
            ApplyPlayerChromeVisuals();
        }
        catch (Exception ex)
        {
            _fullscreenSession.Reset();
            _logger.LogDebug(ex, "[LinuxHome] ExitPlaybackFullscreenIfNeeded failed");
        }
    }

    private static LinuxHostWindowMode ToHostWindowMode(Avalonia.Controls.WindowState state) =>
        state switch
        {
            Avalonia.Controls.WindowState.Maximized => LinuxHostWindowMode.Maximized,
            Avalonia.Controls.WindowState.FullScreen => LinuxHostWindowMode.FullScreen,
            Avalonia.Controls.WindowState.Minimized => LinuxHostWindowMode.Minimized,
            _ => LinuxHostWindowMode.Normal,
        };

    private static Avalonia.Controls.WindowState ToAvaloniaWindowState(LinuxHostWindowMode mode) =>
        mode switch
        {
            LinuxHostWindowMode.Maximized => Avalonia.Controls.WindowState.Maximized,
            LinuxHostWindowMode.FullScreen => Avalonia.Controls.WindowState.FullScreen,
            LinuxHostWindowMode.Minimized => Avalonia.Controls.WindowState.Minimized,
            _ => Avalonia.Controls.WindowState.Normal,
        };

    private static List<Game> FlattenGames(Dictionary<string, List<Game>>? dict)
    {
        if (dict is null || dict.Count == 0)
            return new List<Game>();

        var list = new List<Game>();
        foreach (var pair in dict)
        {
            if (pair.Value is null) continue;
            list.AddRange(pair.Value);
        }

        return list;
    }

    /// <summary>Same decision the other heads make after the native player closed.</summary>
    private void TryResumeAfterPlayer()
    {
        try
        {
            var resolutionActive = _isResolvingStreams
                || _resolutionStartClaimed
                || _resolutionTask is { IsCompleted: false };
            var resume = _homeShell.DecideResumeAfterPlayer(resolutionActive, _selection.CurrentGame, _resolutionExhausted);
            if (resume == ResumeAfterPlayerAction.Clear)
            {
                _selection.CurrentGame = null;
                _homeShell.ClearSelection();
            }
            else if (resume == ResumeAfterPlayerAction.Resume && _homeShell.SelectedGame != null)
            {
                _logger.LogInformation(
                    "[LinuxHome] Resuming stream resolution after native player for {Home} vs {Away}",
                    _homeShell.SelectedGame.DisplayHome, _homeShell.SelectedGame.DisplayAway);
                _ = StartStreamResolutionAsync(_homeShell.SelectedGame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] Resume-after-player check failed");
        }
    }

    private void ShowResolveOverlay(string subtitle) => Dispatcher.Dispatch(() =>
    {
        ResolveTitleLabel.Text = "Finding streams...";
        ResolveStatusLabel.Text = subtitle;
        ResolveStatusLabel.IsVisible = true;
        ApplyResolveWaitVisual(indeterminate: true, fraction: 0);
        ResolveCountLabel.Text = "0 tested • 0 healthy";
        ResolveOverlay.IsVisible = true;
        _resolveOverlayOpen = true;
        ResolveCancelButton.Focus();
    });

    private void UpdateResolveOverlay(StreamResolutionProgress progress) => Dispatcher.Dispatch(() =>
    {
        var isNoHealthy = StreamResolveOverlayProgress.IsExhaustedStatus(progress.Status);

        ResolveTitleLabel.Text = isNoHealthy
            ? string.Empty
            : progress.Status == "Playing..." ? "Now Playing" : "Finding streams...";
        ResolveTitleLabel.IsVisible = ResolveTitleLabel.Text.Length > 0;
        ResolveStatusLabel.Text = progress.Status;
        ResolveStatusLabel.IsVisible = !string.IsNullOrEmpty(progress.Status)
            && !string.Equals(progress.Status, "Searching for streams", StringComparison.OrdinalIgnoreCase);
        ApplyResolveWaitVisual(
            StreamResolveOverlayProgress.IsIndeterminate(progress.TotalStreams, isNoHealthy),
            StreamResolveOverlayProgress.Fraction(progress.StreamsTested, progress.TotalStreams));
        ResolveCountLabel.Text = progress.TotalStreams > 0
            ? $"{progress.TotalStreams} total • {progress.StreamsTested} tested • {progress.HealthyStreams} healthy"
            : $"{progress.StreamsTested} tested • {progress.HealthyStreams} healthy";
        // Keep the modal up for the whole owned session — progress.IsResolving
        // alone can go false on first subscribe while we still owe the overlay.
        ResolveOverlay.IsVisible = _resolveOverlayOpen;
    });

    private void ApplyResolveWaitVisual(bool indeterminate, double fraction)
    {
        ResolveActivityIndicator.IsVisible = indeterminate;
        ResolveActivityIndicator.IsRunning = indeterminate;
        ResolveProgressBar.IsVisible = !indeterminate;
        ResolveProgressBar.Progress = fraction;
    }

    private void ShowStreamPlaybackError(string? message)
    {
        _serviceError = message ?? "Stream unavailable";
        PushErrorBanner();
        _isResolvingStreams = false;
        _resolveOverlayOpen = false;
        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }
}
