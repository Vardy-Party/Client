using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Desktop.Services;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.Streaming;

namespace VardyParty.Desktop.Pages;

/// <summary>
/// Desktop-head host for the shared XAML homepage: the same auth +
/// stream-resolution glue as the MAUI head's HomeHostPage, with two
/// desktop-specific twists — sign-in uses the Auth0 device-code flow with a QR
/// code (ported from the retired VardyParty.Linux head), and playback runs in
/// libvlc's own native window with an in-app "Now playing / Close" overlay
/// (see <see cref="DesktopVideoPlayerService"/>).
/// Set VARDYPARTY_DESKTOP_SAMPLE_DATA=1 to skip auth and render a fabricated
/// catalog (demos and the headless CI smoke test).
/// </summary>
public partial class DesktopHomePage : ContentPage
{
    private readonly ILogger<DesktopHomePage> _logger;
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
    private readonly Auth0Settings _auth0Settings;
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

    // Stream resolution state (mirrors HomeHostPage's fields).
    private bool _isResolvingStreams;
    private bool _resolutionStartClaimed;
    private bool _resolutionExhausted;
    private int _resolutionGeneration;
    private CancellationTokenSource? _resolutionCts;
    private Task? _resolutionTask;

    private static bool UseSampleData =>
        Environment.GetEnvironmentVariable("VARDYPARTY_DESKTOP_SAMPLE_DATA") == "1";

    public DesktopHomePage(
        ILogger<DesktopHomePage> logger,
        HomeViewModel viewModel,
        IEnrichedGameService gameService,
        IStreamResolutionOrchestrator orchestrator,
        INativeVideoPlayerService videoPlayer,
        IAuthTokenProvider authTokens,
        IAuthLoginService authLogin,
        ILocalLanServiceAvailabilityMonitor lanMonitor,
        SelectionState selection,
        UiSoundService sounds,
        IUiSoundPlayer soundPlayer,
        IOptions<Auth0Settings> auth0Settings)
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
        _auth0Settings = auth0Settings.Value;

        InitializeComponent();
        BindingContext = _viewModel;

        _viewModel.GamePicked += OnGamePicked;
        _viewModel.SignOutRequested += () => _ = SignOutAsync();

        // Suppress all UI blips while the native VLC window is open, and show
        // the in-app now-playing card that carries the Close control.
        _videoPlayer.PlaybackVisibilityChanged += OnPlaybackVisibilityChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
            // (DesktopAuthService handles both).
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

    private void OnGamePicked(Game game) => _ = StartStreamResolutionAsync(game);

    private async Task StartStreamResolutionAsync(Game game)
    {
        _logger.LogInformation(
            "[DesktopHome] Starting stream resolution for {Home} vs {Away}", game.DisplayHome, game.DisplayAway);

        if (_resolutionStartClaimed || _resolutionTask is { IsCompleted: false })
        {
            if (_homeShell.SelectedGame != null && HomePlaybackIntent.SameGame(_homeShell.SelectedGame, game))
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
        _resolutionExhausted = false;
        ShowResolveOverlay($"{game.DisplayHome} v {game.DisplayAway}");
        Dispatcher.Dispatch(() => PlaybackTitleLabel.Text = $"{game.DisplayHome} v {game.DisplayAway}");

        _progressSubscription ??= _orchestrator.ProgressUpdated.Subscribe(progress =>
        {
            if (progress.HealthyStreams > 0)
            {
                _homeShell.MarkPlayerSessionStarted();
            }

            _isResolvingStreams = progress.IsResolving;
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
                ShowStreamPlaybackError(ex.Message);
            }
            finally
            {
                if (generation == Volatile.Read(ref _resolutionGeneration))
                {
                    _resolutionStartClaimed = false;
                    _isResolvingStreams = false;
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
        _sounds.Play(UiSound.Back);
        _logger.LogInformation("[DesktopHome] Stream discovery cancelled by user");

        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }

    private void OnResolveCancelClicked(object? sender, EventArgs e) => CancelStreamDiscoveryFromUser();

    /// <summary>Close control for the external VLC window (mirrors the old Linux head's Close button).</summary>
    private void OnClosePlaybackClicked(object? sender, EventArgs e)
    {
        try
        {
            _resolutionCts?.Cancel();
            (_videoPlayer as DesktopVideoPlayerService)?.StopPlayback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Failed to close playback");
        }

        Dispatcher.Dispatch(() => PlaybackOverlay.IsVisible = false);
    }

    private void OnPlaybackVisibilityChanged(object? sender, bool visible)
    {
        _sounds.SuppressAll = visible;
        Dispatcher.Dispatch(() =>
        {
            PlaybackOverlay.IsVisible = visible;
            if (!visible)
            {
                TryResumeAfterPlayer();
            }
        });
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
        Dispatcher.Dispatch(() => ResolveOverlay.IsVisible = false);
    }
}
