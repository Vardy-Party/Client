using System.Collections.ObjectModel;
using System.ComponentModel;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Homepage shell: Netflix-style league rows built from the enriched games
/// dictionary, plus the hide/show leagues menu and the adaptive layout state.
/// Platform heads feed it games and viewport changes; it raises
/// <see cref="GamePicked"/> when the user selects a match.
/// </summary>
public sealed class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILeagueFilterService _leagueFilter;
    private readonly MenuViewModel _menu;
    private readonly IBadgeImageLoader _images;
    private readonly IHomeAssetLocator _assets;
    private readonly IDispatcher _dispatcher;
    private IDictionary<string, List<Game>>? _lastGames;
    private bool _isMenuOpen;
    private string _subtitle = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasGames;

    public HomeViewModel(
        ILeagueFilterService leagueFilter,
        MenuViewModel menu,
        IBadgeImageLoader images,
        IHomeAssetLocator assets,
        IDispatcher dispatcher)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _leagueFilter.Changed += OnFilterChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when the user picks a match.</summary>
    public event Action<Game>? GamePicked;

    /// <summary>Raised on the UI thread after rows are rebuilt, with the visible game count.</summary>
    public event Action<int>? GamesUpdated;

    public HomeLayoutState Layout { get; } = new();

    public ObservableCollection<LeagueRowViewModel> Rows { get; } = new();

    public ObservableCollection<LeagueToggleViewModel> LeagueToggles { get; } = new();

    public int GameCount { get; private set; }

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set
        {
            if (_isMenuOpen == value) return;
            _isMenuOpen = value;
            Raise(nameof(IsMenuOpen));
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        private set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            Raise(nameof(Subtitle));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            Raise(nameof(ErrorMessage));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _errorMessage.Length > 0;

    public bool HasGames
    {
        get => _hasGames;
        private set
        {
            if (_hasGames == value) return;
            _hasGames = value;
            Raise(nameof(HasGames));
            Raise(nameof(ShowEmptyState));
        }
    }

    public bool ShowEmptyState => !HasGames;

    /// <summary>Feed the latest games dictionary. Safe to call from any thread.</summary>
    public void UpdateGames(IDictionary<string, List<Game>>? gamesByLeague)
    {
        _lastGames = gamesByLeague;
        Rebuild();
    }

    /// <summary>Surface a service error banner. Safe to call from any thread.</summary>
    public void SetError(string? message) =>
        _dispatcher.Dispatch(() => ErrorMessage = message ?? string.Empty);

    /// <summary>Reclassify the layout for a new viewport size / idiom.</summary>
    public void SetViewport(double width, double height, bool isTelevision) =>
        Layout.Apply(HomeLayoutClassifier.Classify(width, height, isTelevision));

    public void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

    public void CloseMenu() => IsMenuOpen = false;

    public void ShowAllLeagues() => _menu.ShowAllLeagues();

    public void ResetLeaguesToDefaults() => _menu.ResetToDefaults();

    public void Dispose() => _leagueFilter.Changed -= OnFilterChanged;

    private void OnFilterChanged() => Rebuild();

    private void Rebuild()
    {
        var dict = _lastGames;

        // Pure work off the UI thread; VM/brush construction on it.
        var display = dict == null
            ? new List<Game>()
            : _leagueFilter.FilterGames(dict.ToDisplay());
        var rowModels = HomeRowsBuilder.Build(display);

        _dispatcher.Dispatch(() => Apply(rowModels, display.Count, dict));
    }

    private void Apply(IReadOnlyList<LeagueRowModel> rowModels, int gameCount, IDictionary<string, List<Game>>? dict)
    {
        _menu.RefreshKnownLeagues(dict);
        RefreshLeagueToggles();

        Rows.Clear();
        var rowViewModels = new List<LeagueRowViewModel>(rowModels.Count);
        foreach (var model in rowModels)
        {
            var cards = model.Games
                .Select(game => new MatchCardViewModel(game, Layout, OnCardPicked))
                .ToList();
            var row = new LeagueRowViewModel(model.League, model.HasLiveGames, cards, Layout);
            rowViewModels.Add(row);
            Rows.Add(row);
        }

        GameCount = gameCount;
        HasGames = gameCount > 0;
        var liveCount = rowModels.Sum(r => r.Games.Count(g => g.IsLiveForOrdering));
        Subtitle = liveCount > 0 ? $"{gameCount} games · {liveCount} live" : $"{gameCount} games";

        GamesUpdated?.Invoke(gameCount);

        _ = LoadImagesAsync(rowViewModels);
    }

    private void RefreshLeagueToggles()
    {
        var known = _menu.KnownLeagues;

        // Rebuild only when the league set changed; otherwise sync check states.
        if (LeagueToggles.Count == known.Count
            && LeagueToggles.Select(t => t.Name).SequenceEqual(known, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var toggle in LeagueToggles)
            {
                toggle.Refresh(_menu.IsLeagueVisible(toggle.Name));
            }
            return;
        }

        LeagueToggles.Clear();
        foreach (var league in known)
        {
            LeagueToggles.Add(new LeagueToggleViewModel(
                league,
                _menu.IsLeagueVisible(league),
                (name, visible) => _leagueFilter.SetLeagueVisible(name, visible)));
        }
    }

    private async Task LoadImagesAsync(IReadOnlyList<LeagueRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            var firstGame = row.Cards.FirstOrDefault()?.Game;
            if (firstGame != null)
            {
                var iconPath = _assets.ResolveLeagueLogoPath(firstGame);
                var icon = await _images.LoadLocalAsync(iconPath);
                if (icon != null)
                {
                    _dispatcher.Dispatch(() => row.LeagueIcon = icon);
                }
            }

            foreach (var card in row.Cards)
            {
                var home = await _images.LoadRemoteAsync(card.Game.HomeBadgeUrl);
                var away = await _images.LoadRemoteAsync(card.Game.AwayBadgeUrl);
                if (home != null || away != null)
                {
                    _dispatcher.Dispatch(() =>
                    {
                        if (home != null) card.HomeBadge = home;
                        if (away != null) card.AwayBadge = away;
                    });
                }
            }
        }
    }

    private void OnCardPicked(MatchCardViewModel card) => GamePicked?.Invoke(card.Game);

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
