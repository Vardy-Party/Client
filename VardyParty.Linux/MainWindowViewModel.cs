using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty.Extensions;
using VardyParty.Providers;
using VardyParty.Services;

namespace VardyParty.Linux;

public class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAuthLoginService _authLoginService;
    private readonly IAuthTokenProvider _authTokenProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainWindowViewModel> _logger;

    private bool _isBusy;
    private bool _isAuthenticated;
    private string _statusMessage = "Ready";

    public MainWindowViewModel(
        IAuthLoginService authLoginService,
        IAuthTokenProvider authTokenProvider,
        IServiceProvider serviceProvider,
        ILogger<MainWindowViewModel> logger)
    {
        _authLoginService = authLoginService;
        _authTokenProvider = authTokenProvider;
        _serviceProvider = serviceProvider;
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

    public async Task InitializeAsync()
    {
        IsAuthenticated = await _authTokenProvider.IsAuthenticatedAsync();
        StatusMessage = IsAuthenticated ? "Authenticated" : "Not authenticated";
    }

    public async Task LoginAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Signing in...";

            var result = await _authLoginService.LoginInteractiveAsync();
            if (!result.IsSuccess)
            {
                IsAuthenticated = false;
                StatusMessage = $"Login failed: {result.Error}";
                return;
            }

            IsAuthenticated = true;
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
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record GameListItem(string League, string Fixture, string Kickoff, string Status);
