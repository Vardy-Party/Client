using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Extensions;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Providers;
using VardyParty.Linux.Services;
using VardyParty.Services;

namespace VardyParty.Linux;

public class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private INativeVideoPlayerService? _videoPlayerService;
    private LinuxVideoPlayerService? _linuxVideoPlayerService;
    private readonly IAuthLoginService _authLoginService;
    private readonly IAuthTokenProvider _authTokenProvider;
    private readonly IStreamResolutionOrchestrator _streamResolutionOrchestrator;
    private readonly SelectionState _selectionState;
    private readonly IServiceProvider _serviceProvider;
    private readonly Auth0Settings _auth0Settings;
    private readonly ILogger<MainWindowViewModel> _logger;

    private bool _isBusy;
    private bool _isResolvingStreams;
    private bool _isAuthenticated;
    private bool _isVideoPlaying;
    private string _statusMessage = "Ready";
    private string _deviceVerificationUri = string.Empty;
    private string _deviceUserCode = string.Empty;
    private Bitmap? _deviceQrCode;
    private GameListItem? _selectedGame;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _streamResolutionCts;
    private IDisposable? _progressSubscription;

    public MainWindowViewModel(
        IAuthLoginService authLoginService,
        IAuthTokenProvider authTokenProvider,
        IStreamResolutionOrchestrator streamResolutionOrchestrator,
        SelectionState selectionState,
        IServiceProvider serviceProvider,
        IOptions<Auth0Settings> auth0Settings,
        ILogger<MainWindowViewModel> logger)
    {
        _authLoginService = authLoginService;
        _authTokenProvider = authTokenProvider;
        _streamResolutionOrchestrator = streamResolutionOrchestrator;
        _selectionState = selectionState;
        _serviceProvider = serviceProvider;
        _auth0Settings = auth0Settings.Value;
        _logger = logger;

        _progressSubscription = _streamResolutionOrchestrator.ProgressUpdated
            .Subscribe(progress =>
            {
                _isResolvingStreams = progress.IsResolving;
                OnPropertyChanged(nameof(IsResolvingStreams));

                if (!string.IsNullOrWhiteSpace(progress.Status))
                {
                    StatusMessage = progress.Status;
                }
            });

        // Resolve the video player service
        _videoPlayerService = _serviceProvider.GetService(typeof(INativeVideoPlayerService)) as INativeVideoPlayerService;
        _linuxVideoPlayerService = _videoPlayerService as LinuxVideoPlayerService;
        if (_linuxVideoPlayerService != null)
        {
            _linuxVideoPlayerService.PlaybackVisibilityChanged += OnPlaybackVisibilityChanged;
        }
    }

    public void SetVideoSurfaceHandle(IntPtr handle)
    {
        if (_videoPlayerService != null && handle != IntPtr.Zero)
        {
            try
            {
                if (_videoPlayerService is LinuxVideoPlayerService linuxVideoPlayerService)
                {
                    linuxVideoPlayerService.SetVideoSurfaceHandle(handle);
                    _logger.LogInformation($"[MainWindowViewModel] Set video surface handle: 0x{handle.ToString("X")}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set video surface handle on player service");
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<GameListItem> Games { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotBusy));
            OnPropertyChanged(nameof(CanLoadGames));
        }
    }

    public bool NotBusy => !IsBusy;

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (_isAuthenticated == value) return;
            _isAuthenticated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLoadGames));
            OnPropertyChanged(nameof(ShowAuthPanel));
            OnPropertyChanged(nameof(ShowGamesPanel));
        }
    }

    public bool CanLoadGames => IsAuthenticated && !IsBusy;

    public bool IsResolvingStreams => _isResolvingStreams;

    public bool ShowAuthPanel => !IsAuthenticated;

    public bool ShowGamesPanel => IsAuthenticated && !ShowVideoPanel;

    public bool ShowMainPanel => !ShowVideoPanel;

    public bool ShowVideoPanel => _isVideoPlaying;

    public GameListItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            _selectedGame = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string DeviceVerificationUri
    {
        get => _deviceVerificationUri;
        private set
        {
            if (_deviceVerificationUri == value) return;
            _deviceVerificationUri = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDeviceFlowDetails));
        }
    }

    public string DeviceUserCode
    {
        get => _deviceUserCode;
        private set
        {
            if (_deviceUserCode == value) return;
            _deviceUserCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDeviceFlowDetails));
        }
    }

    public Bitmap? DeviceQrCode
    {
        get => _deviceQrCode;
        private set
        {
            if (ReferenceEquals(_deviceQrCode, value)) return;
            _deviceQrCode?.Dispose();
            _deviceQrCode = value;
            OnPropertyChanged();
        }
    }

    public bool ShowDeviceFlowDetails => !string.IsNullOrWhiteSpace(DeviceVerificationUri) && !string.IsNullOrWhiteSpace(DeviceUserCode);

    public async Task InitializeAsync()
    {
        IsAuthenticated = await _authTokenProvider.IsAuthenticatedAsync();
        StatusMessage = IsAuthenticated ? "Authenticated" : "Sign in required";
        if (IsAuthenticated)
        {
            ClearDeviceFlowDetails();
            await LoadGamesAsync();
            return;
        }

        await StartDeviceFlowLoginAsync();
    }

    public async Task LoginAsync()
    {
        await StartDeviceFlowLoginAsync();
    }

    public async Task LoadGamesAsync()
    {
        if (IsBusy || !IsAuthenticated) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Loading games...";

            var isAuthed = await _authTokenProvider.IsAuthenticatedAsync();
            IsAuthenticated = isAuthed;

            if (!isAuthed)
            {
                StatusMessage = "Please login first";
                return;
            }

            var apiService = _serviceProvider.GetRequiredService<IApiService>();
            var gamesByLeague = await apiService.GetAllGamesAsync(true);
            var displayGames = gamesByLeague.ToDisplay();

            Games.Clear();
            foreach (var game in displayGames)
            {
                Games.Add(new GameListItem(
                    game,
                    game.DisplayLeague,
                    $"{game.DisplayHome} vs {game.DisplayAway}",
                    game.StartUtcForOrdering == DateTime.MaxValue
                        ? ""
                        : game.StartUtcForOrdering.ToLocalTime().ToString("HH:mm"),
                    game.DisplayStatusText()));
            }

            StatusMessage = $"Loaded {Games.Count} games";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load games on Linux UI");
            StatusMessage = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LogoutAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            await _authTokenProvider.LogoutAsync();
            _authCts?.Cancel();
            _streamResolutionCts?.Cancel();
            Games.Clear();
            SelectedGame = null;
            IsAuthenticated = false;
            ClearDeviceFlowDetails();
            StatusMessage = "Signed out";
            await StartDeviceFlowLoginAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout failed");
            StatusMessage = $"Logout failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _authCts?.Cancel();
        _authCts?.Dispose();
        _streamResolutionCts?.Cancel();
        _streamResolutionCts?.Dispose();
        if (_linuxVideoPlayerService != null)
        {
            _linuxVideoPlayerService.PlaybackVisibilityChanged -= OnPlaybackVisibilityChanged;
        }
        _progressSubscription?.Dispose();
        _streamResolutionOrchestrator.Reset();
        DeviceQrCode = null;
    }

    public async Task PlaySelectedGameAsync(GameListItem? item)
    {
        if (item == null || !IsAuthenticated)
        {
            return;
        }

        try
        {
            _streamResolutionCts?.Cancel();
            _streamResolutionCts?.Dispose();
            _streamResolutionCts = new CancellationTokenSource();

            _selectionState.CurrentGame = item.Game;
            StatusMessage = $"Resolving streams for {item.Fixture}...";

            var outcome = await _streamResolutionOrchestrator.StartAsync(item.Game, _streamResolutionCts.Token);
            if (outcome.PlaybackResult is { Success: false })
            {
                StatusMessage = outcome.PlaybackResult.Message ?? "Playback failed";
                return;
            }

            if (outcome.NoWorkingStreams)
            {
                StatusMessage = "No working streams found";
                return;
            }

            if (outcome.UserClosed)
            {
                StatusMessage = "Playback closed";
                return;
            }

            StatusMessage = "Playback complete";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Playback canceled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve/play stream on Linux UI");
            StatusMessage = $"Playback failed: {ex.Message}";
        }
        finally
        {
            SetVideoPlaying(false);
        }
    }

    public void CloseVideoPlayback()
    {
        try
        {
            _streamResolutionCts?.Cancel();

            if (_videoPlayerService is LinuxVideoPlayerService linuxVideoPlayerService)
            {
                linuxVideoPlayerService.StopPlayback();
            }

            StatusMessage = "Playback closed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close playback");
            StatusMessage = "Failed to close playback";
        }
        finally
        {
            SetVideoPlaying(false);
        }
    }

    private async Task StartDeviceFlowLoginAsync()
    {
        if (IsBusy || IsAuthenticated)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Preparing sign-in...";
            _authCts?.Cancel();
            _authCts?.Dispose();
            _authCts = new CancellationTokenSource();

            AuthLoginResult result;
            if (IsLoopbackRedirectUri(_auth0Settings.RedirectUri))
            {
                ClearDeviceFlowDetails();
                result = await _authLoginService.LoginInteractiveAsync();
            }
            else
            {
                var deviceLogin = await _authLoginService.StartDeviceLoginAsync();
                if (deviceLogin?.DeviceCode == null)
                {
                    IsAuthenticated = false;
                    StatusMessage = "Device login unavailable. Check Auth0 settings and device-code grant.";
                    return;
                }

                var code = deviceLogin.DeviceCode.UserCode;
                var verificationUri = deviceLogin.DeviceCode.VerificationUriComplete ?? deviceLogin.DeviceCode.VerificationUri;
                await SetDeviceFlowDetailsAsync(verificationUri, code);
                StatusMessage = $"Scan QR or open {verificationUri} and enter {code}.";

                result = await _authLoginService.PollDeviceLoginAsync(deviceLogin.DeviceCode, _authCts.Token);
            }

            if (!result.IsSuccess)
            {
                IsAuthenticated = false;
                StatusMessage = $"Login failed: {result.Error}";
                return;
            }

            IsAuthenticated = true;
            ClearDeviceFlowDetails();
            StatusMessage = "Signed in";
            await LoadGamesAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Sign-in canceled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux login failed");
            StatusMessage = $"Login error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetDeviceFlowDetailsAsync(string verificationUri, string userCode)
    {
        DeviceVerificationUri = verificationUri;
        DeviceUserCode = userCode;
        DeviceQrCode = await GenerateQrCodeAsync(verificationUri);
    }

    private void ClearDeviceFlowDetails()
    {
        DeviceVerificationUri = string.Empty;
        DeviceUserCode = string.Empty;
        DeviceQrCode = null;
    }

    private async Task<Bitmap?> GenerateQrCodeAsync(string value)
    {
        try
        {
            var qrUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=220x220&data={Uri.EscapeDataString(value)}";
            using var httpClient = new HttpClient();
            var bytes = await httpClient.GetByteArrayAsync(qrUrl);
            await using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate device-flow QR code");
            return null;
        }
    }

    private static bool IsLoopbackRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return false;
        }

        return Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
               && uri.IsLoopback
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetVideoPlaying(bool isVideoPlaying)
    {
        if (_isVideoPlaying == isVideoPlaying)
        {
            return;
        }

        _isVideoPlaying = isVideoPlaying;
        OnPropertyChanged(nameof(ShowVideoPanel));
        OnPropertyChanged(nameof(ShowMainPanel));
        OnPropertyChanged(nameof(ShowGamesPanel));
    }

    private void OnPlaybackVisibilityChanged(object? sender, bool isVisible)
    {
        SetVideoPlaying(isVisible);
    }
}

public sealed record GameListItem(Game Game, string League, string Fixture, string Kickoff, string Status);
