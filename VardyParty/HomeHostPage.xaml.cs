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
    private readonly MatchEventNotificationPolicy _notifications;
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
    /// <summary>
    /// True from overlay show until we explicitly hide it. Must NOT track
    /// <see cref="StreamResolutionProgress.IsResolving"/>: the orchestrator's
    /// BehaviorSubject emits an initial IsResolving=false on first subscribe
    /// (and Reset can emit the same), which cleared Back suppression while
    /// the finding-streams modal was still on screen — Back then exited the
    /// app on Android TV instead of canceling discovery.
    /// </summary>
    private bool _resolveOverlayOpen;
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
        MatchEventNotificationPolicy notifications,
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
        _notifications = notifications;
        _soundPlayer = soundPlayer;

        // Hand the Leanback TV detection (MainApplication) to the shared view
        // BEFORE InitializeComponent builds it: HomeView seeds the layout
        // class synchronously when BindingContext lands, so the first frame
        // already renders TV metrics instead of Desktop-then-zoom.
        HomeUi.Views.HomeView.KnownTelevision = MauiProgram.IsTv;

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

        if (_initialized)
        {
            // Returning from NativeVideoActivity (or any pause): ExoPlayer
            // takes the mixer and SoundPool stays silent until rebuilt.
            // Field: ticks dead until Settings → UI sounds was toggled.
            PlaybackAudioSession.Apply(playbackVisible: false, _sounds, _soundPlayer);
            return;
        }

        _initialized = true;

        // SoundPool brings up MediaCodec decoders; doing that during the first
        // paint on the 32-bit TV raced the homepage layout and ANR'd (Signal
        // Catcher / kick to Android home). Wait until the shell has had time
        // to draw, then load on a background thread.
        _ = DelayThenInitSoundsAsync();

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

        // Yield past the first layout pass before Keystore + catalog start —
        // SecureStorage on Android can stall while the main thread is jammed,
        // and auth+feed kickoff must not compete with first paint.
        _ = InitializeAuthAfterFirstFrameAsync();
    }

    private async Task DelayThenInitSoundsAsync()
    {
        try
        {
            // TV: SoundPool decode contended with cold-start BBC parse / GC —
            // wait past first paint + enrichment burst before bringing up the
            // pool (field: 10s all-at-once load timed out → silent UI).
            var delay = MauiProgram.IsTv ? TimeSpan.FromSeconds(18) : TimeSpan.FromSeconds(5);
            await Task.Delay(delay);
            await _soundPlayer.InitializeAsync();

            // One retry if the first attempt lost the race to Yield/timeout.
            if (MauiProgram.IsTv)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await _soundPlayer.InitializeAsync();
            }
        }
        catch
        {
            // Sounds stay silent; RecoverDevice / Settings toggle can retry.
        }
    }

    private async Task InitializeAuthAfterFirstFrameAsync()
    {
        try
        {
            // Two dispatcher turns + a short delay: let MAUI attach handlers and
            // paint the loading shell (spinning crest) before Keystore work.
            await MainThread.InvokeOnMainThreadAsync(static () => { });
            await Task.Delay(250);
            await InitializeAuthAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HomeHost] Deferred initialize failed");
        }
    }

    private async Task InitializeAuthAsync()
    {
        _logger.LogInformation("[HomeHost] Initialize start");
        // SecureStorage/Keystore must not ride the UI sync context during cold
        // start — on the TV it waited behind multi-second layout Daveys and
        // helped the system decide the app was ANR.
        _isAuthenticated = await Task.Run(_authTokens.IsAuthenticatedAsync).ConfigureAwait(true);
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
        PostUi(() =>
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
            PostUi(() =>
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
        PostUi(() =>
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

        PostUi(() =>
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

        PostUi(() =>
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

    private void SetAuthOverlayVisible(bool visible) => PostUi(() =>
    {
        AuthOverlay.IsVisible = visible;
        if (visible)
        {
            SignInButton.Focus();
        }
    });

    private void SetAuthStatus(string? message) => PostUi(() =>
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
            var sameGame = _homeShell.SelectedGame != null
                && HomePlaybackIntent.SameGame(_homeShell.SelectedGame, game);
            if (HomePlaybackIntent.ShouldIgnoreRepick(sameGame, _resolutionExhausted))
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
        _resolveOverlayOpen = true;
        _resolutionExhausted = false;
        // Suppress Back before the overlay paints — finding-streams can take
        // seconds and Back must cancel discovery, never FinishAndRemoveTask.
        UpdateBackSuppression();
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
                    // Hide + unsuppress together on the UI thread. Clearing
                    // OverlaySuppression before ResolveOverlay.IsVisible=false
                    // left a window where finding-streams was still on screen
                    // but Back called FinishAndRemoveTask (Android TV field).
                    PostUi(() =>
                    {
                        ResolveOverlay.IsVisible = false;
                        UpdateBackSuppression();
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
        _resolveOverlayOpen = false;
        _selection.CurrentGame = null;
        _homeShell.ClearSelection();
        _viewModel.OnStreamResolutionEnded();
#if ANDROID
        // This Back already belonged to the overlay — arm exit grace before
        // suppression drops, so a repeat press against a stale frame cannot
        // FinishAndRemoveTask.
        try
        {
            MainActivity.NoteOverlayBackConsumed();
        }
        catch
        {
        }
#endif
        _sounds.Play(UiSound.Back);
        _logger.LogInformation("[HomeHost] Stream discovery cancelled by user");

        PostUi(() =>
        {
            ResolveOverlay.IsVisible = false;
            UpdateBackSuppression();
            // Overlay Cancel held focus — without this, Android TV lands on
            // the Menu button instead of the game that opened finding-streams.
            HomeSurface.RestoreFocusAfterOverlay();
        });
    }

    private void OnResolveCancelClicked(object? sender, EventArgs e) => CancelStreamDiscoveryFromUser();

    private void OnPlaybackVisibilityChanged(object? sender, bool visible)
    {
        PlaybackAudioSession.Apply(visible, _sounds, _soundPlayer);

        // The homepage stops being the active surface while a stream plays:
        // match events downgrade to toast-only (no sting) per the
        // notification policy table.
        _notifications.IsPlaybackActive = visible;
        if (!visible)
        {
            PostUi(TryResumeAfterPlayer);
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

    private void ShowResolveOverlay(string subtitle) => PostUi(() =>
    {
        ResolveTitleLabel.Text = "Finding streams...";
        ResolveStatusLabel.Text = subtitle;
        ResolveStatusLabel.IsVisible = true;
        ResolveProgressBar.Progress = 0;
        ResolveProgressBar.IsIndeterminate = true;
        ResolveCountLabel.Text = "0 tested • 0 healthy";
        ResolveOverlay.IsVisible = true;
        // Re-assert suppression on the UI thread with the overlay actually
        // visible — covers any race where a prior session's finally cleared
        // flags before this paint.
        _resolveOverlayOpen = true;
        UpdateBackSuppression();
        ResolveCancelButton.Focus();
    });

    private void UpdateResolveOverlay(StreamResolutionProgress progress) => PostUi(() =>
    {
        var isNoHealthy = StreamResolveOverlayProgress.IsExhaustedStatus(progress.Status);

        ResolveTitleLabel.Text = isNoHealthy
            ? string.Empty
            : progress.Status == "Playing..." ? "Now Playing" : "Finding streams...";
        ResolveTitleLabel.IsVisible = ResolveTitleLabel.Text.Length > 0;
        ResolveStatusLabel.Text = progress.Status;
        ResolveStatusLabel.IsVisible = !string.IsNullOrEmpty(progress.Status)
            && !string.Equals(progress.Status, "Searching for streams", StringComparison.OrdinalIgnoreCase);
        ResolveProgressBar.IsIndeterminate =
            StreamResolveOverlayProgress.IsIndeterminate(progress.TotalStreams, isNoHealthy);
        ResolveProgressBar.Progress =
            StreamResolveOverlayProgress.Fraction(progress.StreamsTested, progress.TotalStreams);
        ResolveCountLabel.Text = progress.TotalStreams > 0
            ? $"{progress.TotalStreams} total • {progress.StreamsTested} tested • {progress.HealthyStreams} healthy"
            : $"{progress.StreamsTested} tested • {progress.HealthyStreams} healthy";
        // Keep the modal up for the whole owned session — progress.IsResolving
        // alone can go false while we still owe the user a cancelable overlay.
        ResolveOverlay.IsVisible = _resolveOverlayOpen;
        UpdateBackSuppression();
    });

    private void ShowStreamPlaybackError(string? message)
    {
        _serviceError = message ?? "Stream unavailable";
        PushErrorBanner();
        _isResolvingStreams = false;
        _resolveOverlayOpen = false;
        PostUi(() =>
        {
            ResolveOverlay.IsVisible = false;
            UpdateBackSuppression();
        });
    }

    // ------------------------------------------------------------- android --

#if ANDROID
    /// <summary>
    /// Consume hardware Back for an open homepage overlay. MainActivity must
    /// call this before <c>FinishAndRemoveTask</c> — the static suppression
    /// tracker can lag a frame behind the finding-streams modal, and the
    /// OnBackPressed ExitApp path never reaches the OnBack multicast.
    /// </summary>
    /// <returns>True when Back was handled (do not exit the app).</returns>
    public bool TryHandleHardwareBack()
    {
        if (_viewModel.IsMenuOpen)
        {
            _logger.LogInformation("[HomeHost] Hardware back — closing menu");
            PostUi(_viewModel.CloseMenu);
            return true;
        }

        if (!_isAuthenticated && _deviceCode != null)
        {
            _logger.LogInformation("[HomeHost] Hardware back during device sign-in — canceling");
            CancelSignIn();
            return true;
        }

        if (_resolveOverlayOpen || _isResolvingStreams || _resolutionStartClaimed || ResolveOverlay.IsVisible)
        {
            _logger.LogInformation("[HomeHost] Hardware back — canceling stream resolution");
            CancelStreamDiscoveryFromUser();
            return true;
        }

        return false;
    }

    private void AndroidBackHandler(global::Android.Views.Keycode keyCode) =>
        TryHandleHardwareBack();

    private void AndroidMenuHandler(global::Android.Views.Keycode keyCode) =>
        PostUi(_viewModel.ToggleMenu);
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
            // Own the Back key for the whole resolution session, not only while
            // progress.IsResolving is true — and while the modal is actually
            // painted (IsVisible), even if a background finally already cleared
            // the ownership flags.
            var streamResolve =
                _resolveOverlayOpen ||
                _isResolvingStreams ||
                _resolutionStartClaimed ||
                ResolveOverlay.IsVisible;
            tracker.Set("stream-resolve", streamResolve);
            tracker.Set("menu", _viewModel.IsMenuOpen);
            tracker.Set("device-code-sign-in", _deviceCode != null);
        }
        catch
        {
        }
#endif
    }

    /// <summary>
    /// Run UI work on the page dispatcher. If we are already on the UI thread,
    /// run inline (a queued Dispatch into WinUI layout is a 0xc000027b trigger).
    /// Fail the update and log; do not throw back into CoreMessaging.
    /// </summary>
    private void PostUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        void Run()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HomeHost] UI-thread update failed");
            }
        }

        if (Dispatcher.IsDispatchRequired)
        {
            if (!Dispatcher.Dispatch(Run))
            {
                _logger.LogError("[HomeHost] Dispatcher rejected a UI-thread update");
            }

            return;
        }

        Run();
    }
}
