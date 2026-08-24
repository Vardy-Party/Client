using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using QRCoder;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using LibVLCSharp.Shared;
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
    private static readonly HttpClient BadgeHttpClient = new();

    private INativeVideoPlayerService? _videoPlayerService;
    private LinuxVideoPlayerService? _linuxVideoPlayerService;
    private readonly IAuthLoginService _authLoginService;
    private readonly IAuthTokenProvider _authTokenProvider;
    private readonly IEnrichedGameService _enrichedGameService;
    private readonly ILeagueFilterService _leagueFilter;
    private readonly IStreamResolutionOrchestrator _streamResolutionOrchestrator;
    private readonly SelectionState _selectionState;
    private readonly IServiceProvider _serviceProvider;
    private readonly Auth0Settings _auth0Settings;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dictionary<string, IImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

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
    private IDisposable? _gamesSubscription;
    private IDisposable? _gamesErrorSubscription;

    public MainWindowViewModel(
        IAuthLoginService authLoginService,
        IAuthTokenProvider authTokenProvider,
        IEnrichedGameService enrichedGameService,
        ILeagueFilterService leagueFilter,
        IStreamResolutionOrchestrator streamResolutionOrchestrator,
        SelectionState selectionState,
        IServiceProvider serviceProvider,
        IOptions<Auth0Settings> auth0Settings,
        ILogger<MainWindowViewModel> logger)
    {
        _authLoginService = authLoginService;
        _authTokenProvider = authTokenProvider;
        _enrichedGameService = enrichedGameService;
        _leagueFilter = leagueFilter;
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

        _gamesSubscription = _enrichedGameService.GamesStream.Subscribe(dict =>
        {
            if (dict == null)
            {
                return;
            }

            var displayGames = _leagueFilter.FilterGames(dict.ToDisplay());
            _ = Task.Run(async () =>
            {
                var items = await BuildDisplayGamesAsync(displayGames);
                Dispatcher.UIThread.Post(() =>
                {
                    ApplyDisplayGames(items);
                    StatusMessage = $"Loaded {Games.Count} games";
                });
            });
        });

        _gamesErrorSubscription = _enrichedGameService.ErrorStream.Subscribe(error =>
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return;
            }

            Dispatcher.UIThread.Post(() => { StatusMessage = error; });
        });
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

    public MediaPlayer? VideoMediaPlayer => _linuxVideoPlayerService?.MediaPlayer;

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

            if (_enrichedGameService is EnrichedGameService enrichedGameService)
            {
                enrichedGameService.StartBackgroundPolling();
                StatusMessage = "Live game updates started";
                return;
            }

            var apiService = _serviceProvider.GetRequiredService<IApiService>();
            var gamesByLeague = await apiService.GetAllGamesAsync(true);
            var items = await BuildDisplayGamesAsync(_leagueFilter.FilterGames(gamesByLeague.ToDisplay()));
            ApplyDisplayGames(items);
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
        _gamesSubscription?.Dispose();
        _gamesErrorSubscription?.Dispose();
        _streamResolutionOrchestrator.Reset();
        DeviceQrCode = null;
        foreach (var image in _imageCache.Values)
        {
            if (image is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _imageCache.Clear();
    }

    private void ApplyDisplayGames(IReadOnlyCollection<GameListItem> displayGames)
    {
        Games.Clear();

        foreach (var game in displayGames)
        {
            Games.Add(game);
        }
    }

    private async Task<IReadOnlyCollection<GameListItem>> BuildDisplayGamesAsync(IReadOnlyCollection<Game> displayGames)
    {
        var items = new List<GameListItem>(displayGames.Count);

        foreach (var game in displayGames)
        {
            var homeBadge = await LoadRemoteImageAsync(game.HomeBadgeUrl);
            var awayBadge = await LoadRemoteImageAsync(game.AwayBadgeUrl);
            var hasHomeBadge = homeBadge != null;
            var hasAwayBadge = awayBadge != null;

            var leagueLogoPath = LeagueLogoMapper.GetLogoForLeague(game);
            var leagueIcon = await LoadLocalImageAsync(leagueLogoPath);
            var hasLeagueIcon = leagueIcon != null;

            var statusText = game.DisplayStatusText();
            if (string.IsNullOrWhiteSpace(statusText))
            {
                statusText = FormatGameTime(game.Start);
            }

            var scoreText = game.HomeScore.HasValue && game.AwayScore.HasValue
                ? $"{game.HomeScore}-{game.AwayScore}"
                : "VS";

            items.Add(new GameListItem(
                game,
                game.DisplayLeague,
                game.DisplayHome,
                game.DisplayAway,
                scoreText,
                statusText,
                homeBadge,
                awayBadge,
                hasHomeBadge,
                hasAwayBadge,
                leagueIcon,
                hasLeagueIcon));
        }

        return items;
    }

    private async Task<IImage?> LoadRemoteImageAsync(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (_imageCache.TryGetValue(imageUrl, out var cached))
        {
            return cached;
        }

        try
        {
            var bytes = await BadgeHttpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);
            var extension = Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri)
                ? Path.GetExtension(imageUri.AbsolutePath)
                : Path.GetExtension(imageUrl);
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var image = await CreateSvgImageAsync(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return LoadSvgFromStream(stream, imageUrl);
                }).ConfigureAwait(false);

                if (image != null)
                {
                    _imageCache[imageUrl] = image;
                }
                else
                {
                    _logger.LogWarning("Failed to parse SVG image {ImageUrl} (length {Length})", imageUrl, bytes.Length);
                }
                return image;
            }

            await using var bitmapStream = new MemoryStream(bytes);
            var bitmap = new Bitmap(bitmapStream);
            _imageCache[imageUrl] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load image {ImageUrl}", imageUrl);
            return null;
        }
    }

    private async Task<IImage?> LoadLocalImageAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        if (_imageCache.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        try
        {
            var fileName = Path.GetFileName(relativePath);
            var fullPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "leagues", fileName);
            
            if (!File.Exists(fullPath))
            {
                _logger.LogInformation("League logo not found for {ImagePath}", fullPath);
                return null;
            }

            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var image = await CreateSvgImageAsync(() =>
                {
                    using var stream = File.OpenRead(fullPath);
                    return LoadSvgFromStream(stream, fullPath);
                }).ConfigureAwait(false);

                if (image != null)
                {
                    _imageCache[relativePath] = image;
                }
                else
                {
                    _logger.LogWarning("Failed to parse SVG image {ImagePath}", fullPath);
                }
                return image;
            }

            using var bitmapStream = File.OpenRead(fullPath);
            var bitmap = new Bitmap(bitmapStream);
            _imageCache[relativePath] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load local image {ImagePath}", relativePath);
            return null;
        }
    }

    private static async Task<IImage?> CreateSvgImageAsync(Func<IImage?> factory)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return factory();
        }

        return await Dispatcher.UIThread.InvokeAsync(factory);
    }

    private IImage? LoadSvgFromStream(System.IO.Stream stream, string source)
    {
        try
        {
            return new SvgImage
            {
                Source = SvgSource.LoadFromStream(stream, null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SVG parse threw for {Source}", source);
            return null;
        }
    }

    private static string FormatGameTime(DateTime startTime)
    {
        var local = startTime.ToLocalTime();
        if (local.Date == DateTime.Now.Date)
        {
            return local.ToString("h:mm tt");
        }
        else if (local.Date == DateTime.Now.Date.AddDays(1))
        {
            return $"Tomorrow at {local:h:mm tt}";
        }
        else
        {
            return local.ToString("MMM dd, h:mm tt");
        }
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
            StatusMessage = $"Resolving streams for {item.HomeTeam} vs {item.AwayTeam}...";

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

    private Task<Bitmap?> GenerateQrCodeAsync(string value)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
            var pngQr = new PngByteQRCode(qrData);
            var pngBytes = pngQr.GetGraphic(8);
            using var stream = new MemoryStream(pngBytes);
            return Task.FromResult<Bitmap?>(new Bitmap(stream));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate device-flow QR code");
            return Task.FromResult<Bitmap?>(null);
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

public sealed record GameListItem(
    Game Game,
    string League,
    string HomeTeam,
    string AwayTeam,
    string ScoreText,
    string StatusText,
    IImage? HomeBadge,
    IImage? AwayBadge,
    bool HasHomeBadge,
    bool HasAwayBadge,
    IImage? LeagueIcon,
    bool HasLeagueIcon);
