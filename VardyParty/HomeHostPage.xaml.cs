using Microsoft.Extensions.Logging;
using QRCoder;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.Streaming;

namespace VardyParty;

/// <summary>
/// MAUI-head host for the shared XAML homepage (<c>HomeUi.Views.HomeView</c>),
/// replacing the old BlazorWebView shell (deleted on this branch). The auth
/// and stream-resolution orchestration is the minimal glue ported from the
/// old Blazor Home page: same services, same <see cref="HomeShellViewModel"/>
/// selection rules, same native player wiring.
/// </summary>
public partial class HomeHostPage : ContentPage
{
    private readonly ILogger<HomeHostPage> _logger;
    private readonly HomeViewModel _viewModel;
    private readonly IEnrichedGameService _gameService;
    private readonly IStreamResolutionOrchestrator _orchestrator;
    private readonly INativeVideoPlayerService _videoPlayer;
    private readonly IAuthTokenProvider _authTokens;
    private readonly IAuthLoginService _authLogin;
    private readonly ILocalLanServiceAvailabilityMonitor _lanMonitor;
    private readonly SelectionState _selection;
    private readonly UiSoundService _sounds;
    private readonly IUiSoundPlayer _soundPlayer;
    private readonly HomeShellViewModel _homeShell = new();

    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _progressSubscription;
    private bool _initialized;
    private bool _isAuthenticated;
    private bool _isAuthenticating;
    private CancellationTokenSource? _authCts;
    private AuthDeviceCode? _deviceCode;

    private string? _serviceError;
    private string? _lanWarning;

    // Stream resolution state (mirrors the old Blazor Home page's fields).
    private bool _isResolvingStreams;
    private bool _resolutionStartClaimed;
    private bool _resolutionExhausted;
    private int _resolutionGeneration;
    private CancellationTokenSource? _resolutionCts;
    private Task? _resolutionTask;

    public HomeHostPage(
        ILogger<HomeHostPage> logger,
        HomeViewModel viewModel,
        IEnrichedGameService gameService,
        IStreamResolutionOrchestrator orchestrator,
        INativeVideoPlayerService videoPlayer,
        IAuthTokenProvider authTokens,
        IAuthLoginService authLogin,
        ILocalLanServiceAvailabilityMonitor lanMonitor,
        SelectionState selection,
        UiSoundService sounds,
        IUiSoundPlayer soundPlayer)
    {
        _logger = logger;
        _viewModel = viewModel;
        _gameService = gameService;
        _orchestrator = orchestrator;
        _videoPlayer = videoPlayer;
        _authTokens = authTokens;
        _authLogin = authLogin;
        _lanMonitor = lanMonitor;
        _selection = selection;
        _sounds = sounds;
        _soundPlayer = soundPlayer;

        InitializeComponent();
        BindingContext = _viewModel;

        _viewModel.GamePicked += OnGamePicked;
        _viewModel.GamesUpdated += count =>
            _logger.LogInformation("[HomeHost] Games updated count={Count}", count);
        _viewModel.SignOutRequested += () => _ = SignOutAsync();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.IsMenuOpen))
            {
                UpdateBackSuppression();
            }
        };

        // Suppress all UI blips while the native player is on screen; when it
        // goes away, run the same resume-after-player decision the old Blazor Home page took
        // in OnAfterRenderAsync.
        _videoPlayer.PlaybackVisibilityChanged += OnPlaybackVisibilityChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Re-sync Back suppression with actual overlay state whenever the page
        // (re)appears — the Android flags are process-lifetime statics and must
        // never outlive the overlays that set them.
        UpdateBackSuppression();

        if (_initialized) return;
        _initialized = true;

        // Preload UI sounds on a background task after first render — never in
        // the startup path (this app has startup-perf scar tissue).
        _ = Task.Run(() => _soundPlayer.InitializeAsync());

#if ANDROID
        try
        {
            var remote = MainActivity.RemoteKeyHandler;
            remote.OnBack -= AndroidBackHandler;
            remote.OnBack += AndroidBackHandler;
            remote.OnMenu -= AndroidMenuHandler;
            remote.OnMenu += AndroidMenuHandler;
        }
        catch
        {
        }
#endif

        _subscriptions.Add(_lanMonitor.WarningStream.Subscribe(warning =>
        {
            _lanWarning = warning;
            PushErrorBanner();
        }));

        _ = InitializeAuthAsync();
    }

    private async Task InitializeAuthAsync()
    {
        _logger.LogInformation("[HomeHost] Initialize start");
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

        _logger.LogInformation("[HomeHost] Initialize complete (authenticated={Authenticated})", _isAuthenticated);
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

        _logger.LogInformation("[HomeHost] Sign in pressed (IsTv={IsTv})", MauiProgram.IsTv);
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
            if (MauiProgram.IsTv || !MauiProgram.IsWindowsPackaged)
            {
                await SignInWithDeviceCodeAsync();
            }
            else
            {
                var result = await _authLogin.LoginInteractiveAsync();
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.AccessToken))
                {
                    OnSignedIn();
                }
                else if (LooksLikeMissingRedirectCheck(result.Error))
                {
                    _logger.LogWarning("[HomeHost] Interactive Auth0 login missing redirect check; falling back to device sign-in");
                    SetAuthStatus("Browser sign-in unavailable — use the code below.");
                    await SignInWithDeviceCodeAsync();
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
            _logger.LogWarning(ex, "[HomeHost] Sign-in failed");
            SetAuthStatus(string.IsNullOrWhiteSpace(ex.Message) ? "Sign-in failed." : ex.Message);
        }
        finally
        {
            _isAuthenticating = false;
            _deviceCode = null;
            UpdateBackSuppression();
            Dispatcher.Dispatch(() =>
            {
                DeviceCodePanel.IsVisible = false;
                SignInButton.IsEnabled = true;
                SignInButton.Text = "Sign in — Continue";
            });
        }
    }

    private static bool LooksLikeMissingRedirectCheck(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("redirection check", StringComparison.OrdinalIgnoreCase);

    private async Task SignInWithDeviceCodeAsync()
    {
        var deviceLogin = await _authLogin.StartDeviceLoginAsync();
        if (deviceLogin == null)
        {
            SetAuthStatus("Unable to start device sign-in.");
            return;
        }

        _deviceCode = deviceLogin.DeviceCode;
        ShowDeviceCode(_deviceCode);
        UpdateBackSuppression();

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
        UpdateBackSuppression();
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
        _logger.LogInformation("[HomeHost] Signing out");
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
        UpdateBackSuppression();

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
            _logger.LogWarning(ex, "[HomeHost] Failed to generate QR code locally");
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

    private void OnGamePicked(Game game) => _ = StartStreamResolutionAsync(game);

    private async Task StartStreamResolutionAsync(Game game)
    {
        _logger.LogInformation(
            "[HomeHost] Starting stream resolution for {Home} vs {Away}", game.DisplayHome, game.DisplayAway);

        if (_resolutionStartClaimed || _resolutionTask is { IsCompleted: false })
        {
            if (_homeShell.SelectedGame != null && HomePlaybackIntent.SameGame(_homeShell.SelectedGame, game))
            {
                _logger.LogInformation("[HomeHost] Stream resolution already running for this game");
                return;
            }

            _resolutionCts?.Cancel();
        }

        var generation = Interlocked.Increment(ref _resolutionGeneration);
        _resolutionStartClaimed = true;

        _homeShell.OnUserPicked(game);
        _selection.CurrentGame = game;
        _isResolvingStreams = true;
        _resolutionExhausted = false;
        UpdateBackSuppression();
        ShowResolveOverlay($"{game.DisplayHome} v {game.DisplayAway}");

        _progressSubscription ??= _orchestrator.ProgressUpdated.Subscribe(progress =>
        {
            if (progress.HealthyStreams > 0)
            {
                _homeShell.MarkPlayerSessionStarted();
            }

            _isResolvingStreams = progress.IsResolving;
            UpdateBackSuppression();
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

                if (outcome.UserClosed)
                {
                    _selection.CurrentGame = null;
                    _homeShell.ClearSelection();
                    return;
                }

                if (outcome.NoWorkingStreams)
                {
                    ShowStreamPlaybackError("No working streams found");
                }
                else if (outcome.PlaybackResult is { Success: false } && !outcome.UserClosed)
                {
                    ShowStreamPlaybackError(outcome.PlaybackResult.Message);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[HomeHost] Stream resolution cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HomeHost] Error during stream resolution");
                if (generation != Volatile.Read(ref _resolutionGeneration))
                {
                    return;
                }

                _resolutionExhausted = true;
                _selection.CurrentGame = null;
                _homeShell.ClearSelection();
                ShowStreamPlaybackError(ex.Message);
            }
            finally
            {
                if (generation == Volatile.Read(ref _resolutionGeneration))
                {
                    _resolutionStartClaimed = false;
                    _isResolvingStreams = false;
                    _viewModel.OnStreamResolutionEnded();
                    UpdateBackSuppression();
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
        catch
        {
        }

        try
        {
            _orchestrator.Reset();
        }
        catch
        {
        }

        _progressSubscription?.Dispose();
        _progressSubscription = null;
        Interlocked.Increment(ref _resolutionGeneration);
        _resolutionStartClaimed = false;
        _resolutionExhausted = true;
        _isResolvingStreams = false;
        _selection.CurrentGame = null;
        _homeShell.ClearSelection();
        _viewModel.OnStreamResolutionEnded();
        UpdateBackSuppression();
        _sounds.Play(UiSound.Back);
        _logger.LogInformation("[HomeHost] Stream discovery cancelled by user");

        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }

    private void OnResolveCancelClicked(object? sender, EventArgs e) => CancelStreamDiscoveryFromUser();

    private void OnPlaybackVisibilityChanged(object? sender, bool visible)
    {
        _sounds.SuppressAll = visible;
        if (!visible)
        {
            Dispatcher.Dispatch(TryResumeAfterPlayer);
        }
    }

    /// <summary>Same decision the old Blazor Home page made after the native player closed.</summary>
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
                    "[HomeHost] Resuming stream resolution after native player for {Home} vs {Away}",
                    _homeShell.SelectedGame.DisplayHome, _homeShell.SelectedGame.DisplayAway);
                _ = StartStreamResolutionAsync(_homeShell.SelectedGame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HomeHost] Resume-after-player check failed");
        }
    }

    private void ShowResolveOverlay(string subtitle) => Dispatcher.Dispatch(() =>
    {
        ResolveTitleLabel.Text = "Finding streams...";
        ResolveStatusLabel.Text = subtitle;
        ResolveStatusLabel.IsVisible = true;
        ResolveProgressBar.Progress = 0;
        ResolveCountLabel.Text = "0 tested • 0 healthy";
        ResolveOverlay.IsVisible = true;
        ResolveCancelButton.Focus();
    });

    private void UpdateResolveOverlay(StreamResolutionProgress progress) => Dispatcher.Dispatch(() =>
    {
        var isNoHealthy = !string.IsNullOrEmpty(progress.Status)
            && progress.Status.Contains("No healthy streams", StringComparison.OrdinalIgnoreCase);

        ResolveTitleLabel.Text = isNoHealthy
            ? string.Empty
            : progress.Status == "Playing..." ? "Now Playing" : "Finding streams...";
        ResolveTitleLabel.IsVisible = ResolveTitleLabel.Text.Length > 0;
        ResolveStatusLabel.Text = progress.Status;
        ResolveStatusLabel.IsVisible = !string.IsNullOrEmpty(progress.Status)
            && !string.Equals(progress.Status, "Searching for streams", StringComparison.OrdinalIgnoreCase);
        ResolveProgressBar.Progress = progress.TotalStreams > 0
            ? Math.Clamp((double)progress.StreamsTested / progress.TotalStreams, 0, 1)
            : 0;
        ResolveCountLabel.Text = progress.TotalStreams > 0
            ? $"{progress.TotalStreams} total • {progress.StreamsTested} tested • {progress.HealthyStreams} healthy"
            : $"{progress.StreamsTested} tested • {progress.HealthyStreams} healthy";
        ResolveOverlay.IsVisible = _isResolvingStreams;
    });

    private void ShowStreamPlaybackError(string? message)
    {
        _serviceError = message ?? "Stream unavailable";
        PushErrorBanner();
        _isResolvingStreams = false;
        UpdateBackSuppression();
        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }

    // ------------------------------------------------------------- android --

#if ANDROID
    private void AndroidBackHandler(global::Android.Views.Keycode keyCode)
    {
        if (_viewModel.IsMenuOpen)
        {
            Dispatcher.Dispatch(_viewModel.CloseMenu);
            return;
        }

        if (!_isAuthenticated && _deviceCode != null)
        {
            _logger.LogInformation("[HomeHost] Android back pressed during device sign-in — canceling");
            CancelSignIn();
            return;
        }

        if (_isResolvingStreams)
        {
            _logger.LogInformation("[HomeHost] Android back pressed - canceling stream resolution");
            CancelStreamDiscoveryFromUser();
        }
    }

    private void AndroidMenuHandler(global::Android.Views.Keycode keyCode) =>
        Dispatcher.Dispatch(_viewModel.ToggleMenu);
#endif

    /// <summary>
    /// Back must be consumed (menu close / cancel sign-in / cancel resolution)
    /// instead of exiting the app whenever one of our layers is open. Each
    /// overlay is reported by name so suppression exactly tracks visible
    /// overlays and the Android log can say which one consumed Back.
    /// </summary>
    private void UpdateBackSuppression()
    {
#if ANDROID
        try
        {
            var tracker = MainActivity.OverlaySuppression;
            tracker.Set("stream-resolve", _isResolvingStreams);
            tracker.Set("menu", _viewModel.IsMenuOpen);
            tracker.Set("device-code-sign-in", _deviceCode != null);
        }
        catch
        {
        }
#endif
    }
}
