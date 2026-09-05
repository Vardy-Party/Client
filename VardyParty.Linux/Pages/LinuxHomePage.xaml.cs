using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Linux.Controls;
using VardyParty.Linux.Services;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.Streaming;

namespace VardyParty.Linux.Pages;

/// <summary>
/// Desktop-head host for the shared XAML homepage: the same auth +
/// stream-resolution glue as the MAUI head's HomeHostPage, with two
/// desktop-specific twists — sign-in uses the Auth0 device-code flow with a QR
/// code (ported from the retired VardyParty.Linux head), and playback runs
/// in-window (or libvlc's own window as fallback) with a reserved-airspace
/// Close chip (see <see cref="LinuxVideoPlayerService"/>).
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
    private LinuxPlaybackChromeWindow? _playbackChromeWindow;
    private IDispatcherTimer? _chromePlacementTimer;
    private List<Game> _gamesSnapshot = new();
    private bool _chromeVisible;

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

        // In-playback match-event toast: lives in the reserved airspace row
        // next to Close (never over the native video child — airspace). Same
        // queue/dismiss machine as the homepage toast. Audio stays suppressed
        // during playback via ShouldPlayAudio — toast-yes/audio-no.
        _playbackToast = new MatchEventToastViewModel(_viewModel.Layout);
        PlaybackToast.BindingContext = _playbackToast;
        _playbackToast.PropertyChanged += OnPlaybackToastPropertyChanged;
        _matchEvents.Published += OnMatchEventPublished;
        WireCloseChipGestures();

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
    /// Hosted-surface wiring for in-window playback: the service asks this
    /// page (via <see cref="LinuxVideoPlayerService.EmbedSurfaceAsync"/>) to
    /// assign the MediaPlayer to a hosted VideoHostView BEFORE Play, and tells
    /// it when the surface may be safely detached (clean stop) or must never
    /// be touched again (a wedged libvlc pair was abandoned).
    /// </summary>
    private void WireEmbeddedVideoHost()
    {
        if (_videoPlayer is not LinuxVideoPlayerService service)
        {
            return;
        }

        service.EmbedSurfaceAsync = EmbedSurfaceOnUiAsync;
        service.DetachSurfaceRequested += OnDetachSurfaceRequested;
        service.SurfacePoisoned += OnSurfacePoisoned;
        service.EmbeddingStateChanged += OnEmbeddingStateChanged;
    }

    /// <summary>
    /// UI-thread half of the embed handshake: make the video row host a
    /// (fresh or reused) VideoHostView and assign the player so VideoView
    /// attaches the drawable. The service polls the drawable and decides
    /// embedded vs standalone-fallback; this method must never block its
    /// caller (always dispatches) and never touches libvlc beyond the
    /// non-blocking MediaPlayer property assignment.
    /// </summary>
    private Task EmbedSurfaceOnUiAsync(LibVLCSharp.Shared.MediaPlayer player)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Dispatch(() =>
        {
            try
            {
                if (_videoHost == null)
                {
                    _videoHost = new Controls.VideoHostView();
                    VideoHostContainer.Children.Add(_videoHost);
                }

                // The native child window only realizes while the host is in
                // the visible tree — show the video row before assigning.
                VideoHostContainer.IsVisible = true;
                StandalonePlaybackPanel.IsVisible = false;
                _videoHost.MediaPlayer = player;
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Clean stop: the player is idle, clearing the binding is a safe no-op detach.</summary>
    private void OnDetachSurfaceRequested() => Dispatcher.Dispatch(() =>
    {
        if (_videoHost != null)
        {
            _videoHost.MediaPlayer = null;
        }
    });

    /// <summary>
    /// A libvlc pair was abandoned as wedged: park the current host invisible
    /// and forget it — removing it (or clearing its MediaPlayer) would run
    /// VideoView's drawable-detach against the wedged player, which can hold
    /// its object lock and stall the UI thread. The next session builds a
    /// fresh host; the parked one leaks with its abandoned player by design.
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
            _logger.LogDebug(ex, "[DesktopHome] Escape-to-close wiring skipped");
        }
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !PlaybackOverlay.IsVisible)
        {
            return;
        }

        if (_playbackChrome?.TryDismissLayer() == true)
        {
            e.Handled = true;
            return;
        }

        OnClosePlaybackClicked(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnTopLevelPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!PlaybackOverlay.IsVisible || _playbackTopLevel is not { } top)
        {
            return;
        }

        var pos = e.GetPosition(top);
        if (LinuxCloseChipReveal.IsNearRestingPlace(pos.X, pos.Y, top.Bounds.Width, _closeChip.IsRevealed))
        {
            ApplyCloseChip(_closeChip.OnHoverEnter());
        }
        else if (_closeChip.Hovering)
        {
            ApplyCloseChip(_closeChip.OnHoverLeave());
        }
    }

    private void OnTopLevelPointerPressed(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!PlaybackOverlay.IsVisible)
        {
            return;
        }

        // A press we can see (reserved chrome / standalone card). Presses on
        // the native video child never reach Avalonia — the thin hit-zone
        // is the in-window path. Does not close; the chip click does.
        ApplyCloseChip(_closeChip.OnTouched());
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
            PlaybackChromeRow.MinimumHeightRequest = revealed
                ? LinuxCloseChipReveal.RevealedReserveHeight
                : 0;
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
            PushErrorBanner();
        }));

        if (UseSampleData)
        {
            _logger.LogInformation("[DesktopHome] Sample data mode: skipping auth");
            _viewModel.UpdateGames(SampleGames.Build());

            // Exercise the in-place diff path (goal, minute ticks, add/remove,
            // live-set re-tier) on the real UI a few seconds in — the headless
            // CI smoke keeps the app alive for ~20s, so a crash in the refresh
            // path fails the gate instead of shipping.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                _logger.LogInformation("[DesktopHome] Sample data mode: applying refreshed board");
                _viewModel.UpdateGames(SampleGames.BuildRefreshed());
            });
            return;
        }

        _ = InitializeAuthAsync();
    }

    private async Task InitializeAuthAsync()
    {
        _logger.LogInformation("[DesktopHome] Initialize start");
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

        _logger.LogInformation("[DesktopHome] Initialize complete (authenticated={Authenticated})", _isAuthenticated);
    }

    private void StartGamesFeed()
    {
        _subscriptions.Add(_gameService.GamesStream.Subscribe(dict => _viewModel.UpdateGames(dict)));
        _subscriptions.Add(_gameService.ErrorStream.Subscribe(error =>
        {
            _serviceError = error;
            PushErrorBanner();
        }));

        (_gameService as EnrichedGameService)?.StartBackgroundPolling();
    }

    /// <summary>Service errors outrank the LAN warning; one shared banner.</summary>
    private void PushErrorBanner() =>
        _viewModel.SetError(!string.IsNullOrWhiteSpace(_serviceError) ? _serviceError : _lanWarning);

    // ---------------------------------------------------------------- auth --

    private async void OnSignInClicked(object? sender, EventArgs e) => await StartSignInAsync();

    private void OnCancelSignInClicked(object? sender, EventArgs e) => CancelSignIn();

    private async Task StartSignInAsync()
    {
        if (_isAuthenticating) return;

        _logger.LogInformation("[DesktopHome] Sign in pressed");
        _isAuthenticating = true;
        _deviceCode = null;
        Dispatcher.Dispatch(() =>
        {
            SignInButton.IsEnabled = false;
            SignInButton.Text = "Signing in…";
            SetAuthStatus(null);
        });

        try
        {
            // The desktop RedirectUri is the custom vardyparty:// scheme (not
            // loopback), so the device-code flow with QR is the standard path;
            // a loopback redirect would enable the browser PKCE flow instead
            // (LinuxAuthService handles both).
            if (Auth0Pkce.TryGetLoopbackRedirectUri(_auth0Settings.RedirectUri, out _))
            {
                var result = await _authLogin.LoginInteractiveAsync();
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.AccessToken))
                {
                    OnSignedIn();
                }
                else if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    SetAuthStatus(result.Error);
                }
            }
            else
            {
                var deviceLogin = await _authLogin.StartDeviceLoginAsync();
                if (deviceLogin == null)
                {
                    SetAuthStatus("Unable to start device sign-in.");
                    return;
                }

                _deviceCode = deviceLogin.DeviceCode;
                ShowDeviceCode(_deviceCode);

                _authCts?.Cancel();
                _authCts = new CancellationTokenSource();

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
        }
        catch (OperationCanceledException)
        {
            SetAuthStatus("Sign-in canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Sign-in failed");
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
        _logger.LogInformation("[DesktopHome] Signing out");
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
            _viewModel.SetError(null);
            _viewModel.ResetScoreObservations();
            SetAuthOverlayVisible(true);
        });

        // The LAN warning stream keeps running across sign-in sessions.
        _subscriptions.Add(_lanMonitor.WarningStream.Subscribe(warning =>
        {
            _lanWarning = warning;
            PushErrorBanner();
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
            _logger.LogWarning(ex, "[DesktopHome] Failed to generate QR code locally");
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
            "[DesktopHome] TEST MEDIA hook: playing {Url} for '{Title}' (stream resolution bypassed)",
            mediaUrl, title);
        try
        {
            var result = await _videoPlayer.PlayVideoAsync(
                mediaUrl, refererUrl: string.Empty, title,
                league: game.League, homeTeam: game.DisplayHome, awayTeam: game.DisplayAway);
            _logger.LogInformation(
                "[DesktopHome] TEST MEDIA playback ended (success={Success}, message={Message})",
                result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] TEST MEDIA playback failed");
        }
        finally
        {
            _viewModel.OnStreamResolutionEnded();
        }
    }

    private async Task StartStreamResolutionAsync(Game game)
    {
        _logger.LogInformation(
            "[DesktopHome] Starting stream resolution for {Home} vs {Away}", game.DisplayHome, game.DisplayAway);

        if (_resolutionStartClaimed || _resolutionTask is { IsCompleted: false })
        {
            var sameGame = _homeShell.SelectedGame != null
                && HomePlaybackIntent.SameGame(_homeShell.SelectedGame, game);
            if (HomePlaybackIntent.ShouldIgnoreRepick(sameGame, _resolutionExhausted))
            {
                _logger.LogInformation("[DesktopHome] Stream resolution already running for this game");
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
                _logger.LogInformation("[DesktopHome] Stream resolution cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DesktopHome] Error during stream resolution");
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
            _logger.LogDebug(ex, "[DesktopHome] Cancel of resolution CTS failed");
        }

        try
        {
            _orchestrator.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] Orchestrator reset on cancel failed");
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
        _logger.LogInformation("[DesktopHome] Stream discovery cancelled by user");

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
                return true;

            OnClosePlaybackClicked(this, EventArgs.Empty);
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
                _logger.LogDebug(ex, "[DesktopHome] Chrome Exit during close failed");
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
            _logger.LogWarning(ex, "[DesktopHome] Failed to close playback");
        }

        Dispatcher.Dispatch(() =>
        {
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
            cleanupPool: () =>
            {
                try { _switching.Cleanup(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[DesktopHome] Switching cleanup failed");
                }
            });

        _playbackChrome.ExitRequested += (_, _) =>
            Dispatcher.Dispatch(CompletePlaybackClose);
        _playbackChrome.StateChanged += (_, _) =>
            Dispatcher.Dispatch(RefreshChromeScoresText);

        return _playbackChrome;
    }

    private void ShowPlaybackChrome()
    {
        try
        {
            var chrome = EnsurePlaybackChrome();
            _playbackChromeWindow ??= new LinuxPlaybackChromeWindow(chrome);

            if (_playbackTopLevel is Avalonia.Controls.Window owner)
                _playbackChromeWindow.Show(owner);
            else
                _playbackChromeWindow.Show();

            _chromeVisible = true;
            WireChromeDataFeeds();
            PushOverlayInfoFromSwitching();
            RefreshChromeScoresText();
            SyncChromePlacement();
            ArmChromePlacementTimer();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Failed to show Avalonia playback chrome");
        }
    }

    private void HidePlaybackChrome()
    {
        _chromeVisible = false;
        _chromePlacementTimer?.Stop();

        foreach (var sub in _chromeSubscriptions)
        {
            try { sub.Dispose(); }
            catch { /* ignore */ }
        }
        _chromeSubscriptions.Clear();

        try
        {
            _playbackChromeWindow?.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] Hide playback chrome failed");
        }
    }

    private void WireChromeDataFeeds()
    {
        if (_chromeSubscriptions.Count > 0)
            return;

        _chromeSubscriptions.Add(_switching.OverlayInfoChanged.Subscribe(info =>
            Dispatcher.Dispatch(() => ApplyOverlayInfo(info))));

        _chromeSubscriptions.Add(_switching.HealthyStreamsUpdated.Subscribe(list =>
            Dispatcher.Dispatch(() =>
            {
                _playbackChrome?.NotifyHealthyCount(list.Count);
                PushOverlayInfoFromSwitching();
            })));

        _chromeSubscriptions.Add(_gameService.GamesStream.Subscribe(dict =>
        {
            _gamesSnapshot = FlattenGames(dict);
            Dispatcher.Dispatch(RefreshChromeScoresText);
        }));
    }

    private void PushOverlayInfoFromSwitching()
    {
        try
        {
            var current = _switching.GetCurrentStream();
            var total = _switching.GetHealthyStreams().Count;
            var index = _switching.GetCurrentStreamIndex();
            var info = LinuxPlaybackChromeInfoText.BuildOverlayInfo(
                current, index, total, current?.Referer);
            ApplyOverlayInfo(info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] PushOverlayInfoFromSwitching failed");
        }
    }

    private void ApplyOverlayInfo(PlayerOverlayInfo? info)
    {
        if (_playbackChrome is null)
            return;

        _playbackChrome.ApplyOverlayInfo(info);
        if (info is not null)
            _playbackChrome.NotifyHealthyCount(info.Total);

        if (_playbackChromeWindow is null)
            return;

        _playbackChromeWindow.SetVideoInfoBody(
            info is null ? string.Empty : LinuxPlaybackChromeInfoText.FormatVideoInfo(info));
        _playbackChromeWindow.SetSourceBadge(
            _switching.GetCurrentStream()?.Stream?.CatalogSourceBadgeLabel);
    }

    private void RefreshChromeScoresText()
    {
        if (_playbackChromeWindow is null || _playbackChrome is null)
            return;

        var league = _selection.CurrentGame?.DisplayLeague
            ?? _homeShell.SelectedGame?.DisplayLeague;
        _playbackChromeWindow.SetScoresText(
            LinuxPlaybackChromeInfoText.FormatScoresTicker(
                _gamesSnapshot, _playbackChrome.ScoresMode, league));
    }

    private void ArmChromePlacementTimer()
    {
        _chromePlacementTimer ??= CreateChromePlacementTimer();
        _chromePlacementTimer.Stop();
        _chromePlacementTimer.Start();
    }

    private IDispatcherTimer CreateChromePlacementTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) =>
        {
            if (!_chromeVisible)
            {
                timer.Stop();
                return;
            }

            SyncChromePlacement();
        };
        return timer;
    }

    private void SyncChromePlacement()
    {
        if (_playbackChromeWindow is null || _playbackTopLevel is null)
            return;

        try
        {
            var hostVisual = ResolvePlaybackHostVisual();
            if (hostVisual is null)
                return;

            var chromeRowHeight = Math.Max(0, PlaybackChromeRow.Height);
            if (chromeRowHeight <= 0)
                chromeRowHeight = PlaybackChromeRow.MinimumHeightRequest > 0
                    ? PlaybackChromeRow.MinimumHeightRequest
                    : LinuxCloseChipReveal.HiddenReserveHeight;

            // When the overlay covers the full page, subtract the reserved row.
            // VideoHostContainer / Standalone panel share the video row; prefer
            // the visible one's platform visual when available.
            var topLeft = hostVisual.PointToScreen(new Avalonia.Point(0, 0));
            if (!LinuxPlaybackChromePlacement.TryComputeVideoRowBounds(
                    topLeft.X,
                    topLeft.Y,
                    hostVisual.Bounds.Width,
                    hostVisual.Bounds.Height,
                    IsVideoRowVisual(hostVisual) ? 0 : chromeRowHeight,
                    out var x,
                    out var y,
                    out var width,
                    out var height))
            {
                return;
            }

            _playbackChromeWindow.PlaceOver(new Avalonia.PixelRect(
                (int)Math.Round(x),
                (int)Math.Round(y),
                (int)Math.Round(width),
                (int)Math.Round(height)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] SyncChromePlacement failed");
        }
    }

    private Avalonia.Visual? ResolvePlaybackHostVisual()
    {
#if EMBEDDED_LINUX_VIDEO
        if (VideoHostContainer.IsVisible &&
            VideoHostContainer.Handler?.PlatformView is Avalonia.Visual videoVisual)
        {
            return videoVisual;
        }
#endif
        if (StandalonePlaybackPanel.IsVisible &&
            StandalonePlaybackPanel.Handler?.PlatformView is Avalonia.Visual standaloneVisual)
        {
            return standaloneVisual;
        }

        if (PlaybackOverlay.Handler?.PlatformView is Avalonia.Visual overlayVisual)
            return overlayVisual;

        return null;
    }

    private bool IsVideoRowVisual(Avalonia.Visual visual)
    {
#if EMBEDDED_LINUX_VIDEO
        if (VideoHostContainer.Handler?.PlatformView is Avalonia.Visual video &&
            ReferenceEquals(video, visual))
        {
            return true;
        }
#endif
        return StandalonePlaybackPanel.Handler?.PlatformView is Avalonia.Visual standalone
            && ReferenceEquals(standalone, visual);
    }

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
                    "[DesktopHome] Resuming stream resolution after native player for {Home} vs {Away}",
                    _homeShell.SelectedGame.DisplayHome, _homeShell.SelectedGame.DisplayAway);
                _ = StartStreamResolutionAsync(_homeShell.SelectedGame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] Resume-after-player check failed");
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
