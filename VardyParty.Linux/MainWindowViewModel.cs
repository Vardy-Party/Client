using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Extensions;
using VardyParty.Providers;
using VardyParty.Services;

namespace VardyParty.Linux;

public class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAuthLoginService _authLoginService;
    private readonly IAuthTokenProvider _authTokenProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly Auth0Settings _auth0Settings;
    private readonly ILogger<MainWindowViewModel> _logger;

    private bool _isBusy;
    private bool _isAuthenticated;
    private string _statusMessage = "Ready";
    private string _deviceVerificationUri = string.Empty;
    private string _deviceUserCode = string.Empty;
    private Bitmap? _deviceQrCode;

    public MainWindowViewModel(
        IAuthLoginService authLoginService,
        IAuthTokenProvider authTokenProvider,
        IServiceProvider serviceProvider,
        IOptions<Auth0Settings> auth0Settings,
        ILogger<MainWindowViewModel> logger)
    {
        _authLoginService = authLoginService;
        _authTokenProvider = authTokenProvider;
        _serviceProvider = serviceProvider;
        _auth0Settings = auth0Settings.Value;
        _logger = logger;
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
        }
    }

    public bool CanLoadGames => IsAuthenticated && !IsBusy;

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
        StatusMessage = IsAuthenticated ? "Authenticated" : "Not authenticated";
        if (IsAuthenticated)
        {
            ClearDeviceFlowDetails();
        }
    }

    public async Task LoginAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Signing in...";

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
                    StatusMessage = "Login failed: device login unavailable. Check Auth0 Domain/ClientId and device-code grant.";
                    return;
                }

                var code = deviceLogin.DeviceCode.UserCode;
                var verificationUri = deviceLogin.DeviceCode.VerificationUriComplete ?? deviceLogin.DeviceCode.VerificationUri;
                await SetDeviceFlowDetailsAsync(verificationUri, code);
                StatusMessage = $"Open {verificationUri} and enter code {code}. Waiting for approval...";
                result = await _authLoginService.PollDeviceLoginAsync(deviceLogin.DeviceCode);
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

    public async Task LoadGamesAsync()
    {
        if (IsBusy) return;

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
            Games.Clear();
            IsAuthenticated = false;
            ClearDeviceFlowDetails();
            StatusMessage = "Logged out";
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
        DeviceQrCode = null;
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
}

public sealed record GameListItem(string League, string Fixture, string Kickoff, string Status);
