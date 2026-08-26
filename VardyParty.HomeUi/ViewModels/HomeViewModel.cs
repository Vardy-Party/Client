using System.Collections.ObjectModel;
using System.ComponentModel;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Homepage shell: Netflix-style league rows built from the enriched games
/// dictionary, plus the hide/show leagues menu and the adaptive layout state.
/// Platform heads feed it games and viewport changes; it raises
/// <see cref="GamePicked"/> when the user picks a match.
/// </summary>
public sealed class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILeagueFilterService _leagueFilter;
    private readonly MenuViewModel _menu;
    private readonly IBadgeImageLoader _images;
    private readonly IHomeAssetLocator _assets;
    private readonly UiSoundService _sounds;
    private readonly ScoreChangeDetector _scoreChanges = new();
    private readonly object _pendingLock = new();
    private readonly Queue<Action> _pendingUiAssign = new();
    private IDictionary<string, List<Game>>? _lastGames;
    private bool _isMenuOpen;
    private string _subtitle = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasGames;
    private bool _isContentLoading = true;
    private Game? _resolvingGame;
    private PendingApply? _pendingApply;
    private string? _pendingError;
    private bool _pendingClearResolving;
    private bool _pendingResetScores;

    private sealed record PendingApply(
        IReadOnlyList<LeagueRowModel> Rows,
        IReadOnlyList<Game> Display,
        IDictionary<string, List<Game>>? Dict);

    public HomeViewModel(
        ILeagueFilterService leagueFilter,
        MenuViewModel menu,
        IBadgeImageLoader images,
        IHomeAssetLocator assets,
        IDispatcher dispatcher,
        UiSoundService sounds)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _ = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));

        _leagueFilter.Changed += OnFilterChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when the user picks a match.</summary>
    public event Action<Game>? GamePicked;

    /// <summary>Raised when the user taps "Sign out" in the settings menu (head-handled).</summary>
    public event Action? SignOutRequested;

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

    /// <summary>
    /// Empty copy only after a real catalog has arrived. Startup coalescing can
    /// withhold the first board while BBC is in flight; that is still loading.
    /// </summary>
    public bool ShowEmptyState => !_hasGames && !_isContentLoading;

    /// <summary>
    /// True until the first real games dictionary lands (and again after a
    /// sign-out clears the feed). This is the crest's spin signal: it reflects
    /// actual catalog readiness, never a timer — an empty-but-delivered
    /// catalog counts as ready (the empty state shows; the crest rests).
    /// </summary>
    public bool IsContentLoading
    {
        get => _isContentLoading;
        private set
        {
            if (_isContentLoading == value) return;
            _isContentLoading = value;
            Raise(nameof(IsContentLoading));
            Raise(nameof(ShowEmptyState));
        }
    }

    /// <summary>Feed the latest games dictionary. Safe to call from any thread.</summary>
    public void UpdateGames(IDictionary<string, List<Game>>? gamesByLeague)
    {
        _lastGames = gamesByLeague;
        Rebuild();
    }

    /// <summary>Surface a service error banner. Safe to call from any thread.</summary>
    public void SetError(string? message)
    {
        lock (_pendingLock)
        {
            _pendingError = message ?? string.Empty;
        }
    }

    /// <summary>Reclassify the layout for a new viewport size / idiom.</summary>
    public void SetViewport(double width, double height, bool isTelevision) =>
        Layout.Apply(HomeLayoutClassifier.Classify(width, height, isTelevision));

    public void ToggleMenu()
    {
        var opening = !IsMenuOpen;
        IsMenuOpen = opening;
        if (opening)
        {
            RefreshLeagueToggles();
        }

        _sounds.Play(opening ? UiSound.MenuOpen : UiSound.Back);
    }

    public void CloseMenu()
    {
        if (!IsMenuOpen) return;
        IsMenuOpen = false;
        _sounds.Play(UiSound.Back);
    }

    public void ShowAllLeagues() => _menu.ShowAllLeagues();

    public void ResetLeaguesToDefaults() => _menu.ResetToDefaults();

    /// <summary>Settings: the persisted "UI sounds" switch (default ON).</summary>
    public bool UiSoundsEnabled
    {
        get => _menu.UiSoundsEnabled;
        set
        {
            if (_menu.UiSoundsEnabled == value) return;
            _menu.ToggleUiSounds();
            Raise(nameof(UiSoundsEnabled));
        }
    }

    private bool _canSignOut;

    /// <summary>Heads with a real auth session show the "Sign out" entry.</summary>
    public bool CanSignOut
    {
        get => _canSignOut;
        set
        {
            if (_canSignOut == value) return;
            _canSignOut = value;
            Raise(nameof(CanSignOut));
        }
    }

    public void RequestSignOut()
    {
        _sounds.Play(UiSound.Select);
        SignOutRequested?.Invoke();
    }

    /// <summary>Focus landed on a card or menu item (throttled tick).</summary>
    public void OnFocusPulse() => _sounds.Play(UiSound.FocusMove);

    /// <summary>
    /// Forget observed scores (e.g. on sign-out) so re-appearing games stay
    /// silent. Queued for the UI-thread apply pump — the detector is only
    /// touched there (see <see cref="FlushPendingApply"/>).
    /// </summary>
    public void ResetScoreObservations()
    {
        lock (_pendingLock)
        {
            _pendingResetScores = true;
        }
    }

    public void Dispose() => _leagueFilter.Changed -= OnFilterChanged;

    private void OnFilterChanged() => Rebuild();

    private void Rebuild()
    {
        var dict = _lastGames;

        // Filter/group off the caller thread. WinAppSDK 1.8 stows a
        // 0xc000027b if we Dispatcher.Dispatch from the Rx/HTTP thread into
        // WinUI layout; HomeView's UI-thread timer drains this queue instead.
        var display = dict == null
            ? new List<Game>()
            : _leagueFilter.FilterGames(dict.ToDisplay());
        var rowModels = HomeRowsBuilder.Build(display);

        lock (_pendingLock)
        {
            _pendingApply = new PendingApply(rowModels, display, dict);
        }
    }

    /// <summary>
    /// Drain catalog/error/image work that arrived off the UI thread. Must run
    /// on the UI thread (HomeView's apply pump).
    /// </summary>
    public void FlushPendingApply()
    {
        string? error;
        PendingApply? apply;
        var clearResolving = false;
        var resetScores = false;
        lock (_pendingLock)
        {
            error = _pendingError;
            _pendingError = null;
            apply = _pendingApply;
            _pendingApply = null;
            clearResolving = _pendingClearResolving;
            _pendingClearResolving = false;
            resetScores = _pendingResetScores;
            _pendingResetScores = false;
        }

        if (resetScores)
        {
            _scoreChanges.Reset();
        }

        if (error != null)
        {
            if (error.Length > 0 && _errorMessage.Length == 0)
            {
                _sounds.Play(UiSound.Error);
            }

            ErrorMessage = error;
        }

        if (clearResolving)
        {
            foreach (var card in EnumerateCards())
            {
                card.IsResolving = false;
            }
        }

        if (apply != null)
        {
            if (_scoreChanges.Observe(apply.Display).Count > 0)
            {
                _sounds.Play(UiSound.Goal);
            }

            Apply(apply.Rows, apply.Display.Count, apply.Dict);
        }

        TryApplyOnePendingImage();
        TryApplyOnePendingImage();
        TryApplyOnePendingImage();
        TryApplyOnePendingImage();
    }

    private void Apply(IReadOnlyList<LeagueRowModel> rowModels, int gameCount, IDictionary<string, List<Game>>? dict)
    {
        _menu.RefreshKnownLeagues(dict);
        if (IsMenuOpen)
        {
            RefreshLeagueToggles();
        }

        if (dict == null)
        {
            _resolvingGame = null;
        }

        var hadGames = GameCount > 0;
        lock (_pendingLock)
        {
            _pendingUiAssign.Clear();
        }

        Rows.Clear();
        var rowViewModels = new List<LeagueRowViewModel>(rowModels.Count);
        foreach (var model in rowModels)
        {
            var cards = model.Games
                .Select(game => new MatchCardViewModel(game, Layout, OnCardPicked, OnCardFocused)
                {
                    IsResolving = HomePlaybackIntent.SameGame(_resolvingGame, game),
                })
                .ToList();
            var row = new LeagueRowViewModel(model.League, model.HasLiveGames, cards, Layout);
            rowViewModels.Add(row);
            Rows.Add(row);
        }

        if (!hadGames && rowViewModels.Count > 0 && rowViewModels[0].Cards.Count > 0)
        {
            rowViewModels[0].Cards[0].RequestsInitialFocus = true;
        }

        GameCount = gameCount;
        IsContentLoading = dict == null;
        HasGames = gameCount > 0;
        var liveCount = rowModels.Sum(r => r.Games.Count(g => g.IsLiveForOrdering));
        Subtitle = FormatSubtitle(gameCount, liveCount);

        GamesUpdated?.Invoke(gameCount);

        _ = LoadImagesAsync(rowViewModels);
    }

    private void EnqueueUiAssign(Action assign)
    {
        lock (_pendingLock)
        {
            _pendingUiAssign.Enqueue(assign);
        }
    }

    private void TryApplyOnePendingImage()
    {
        Action? assign;
        lock (_pendingLock)
        {
            if (_pendingUiAssign.Count == 0) return;
            assign = _pendingUiAssign.Dequeue();
        }

        assign();
    }

    private void RefreshLeagueToggles()
    {
        var known = _menu.KnownLeagues;

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
                var iconPath = await _assets.ResolveLeagueLogoPathAsync(firstGame).ConfigureAwait(false);
                var icon = await _images.LoadLocalAsync(iconPath).ConfigureAwait(false);
                if (icon != null)
                {
                    EnqueueUiAssign(() => row.LeagueIcon = icon);
                }
            }

            foreach (var card in row.Cards)
            {
                var home = await _images.LoadRemoteAsync(card.Game.HomeBadgeUrl).ConfigureAwait(false);
                var away = await _images.LoadRemoteAsync(card.Game.AwayBadgeUrl).ConfigureAwait(false);
                if (home == null && away == null) continue;
                EnqueueUiAssign(() =>
                {
                    if (home != null) card.HomeBadge = home;
                    if (away != null) card.AwayBadge = away;
                });
            }
        }
    }

    private static string FormatSubtitle(int gameCount, int liveCount)
    {
        var games = gameCount == 1 ? "1 game" : $"{gameCount} games";
        return liveCount > 0 ? $"{games} · {liveCount} live" : games;
    }

    private IEnumerable<MatchCardViewModel> EnumerateCards() =>
        Rows.SelectMany(r => r.Cards);

    private void OnCardPicked(MatchCardViewModel card)
    {
        _sounds.Play(UiSound.Select);

        _resolvingGame = card.Game;
        foreach (var other in EnumerateCards())
        {
            other.IsResolving = ReferenceEquals(other, card);
        }

        GamePicked?.Invoke(card.Game);
    }

    /// <summary>
    /// Heads call this when stream resolution finishes, fails or is cancelled,
    /// so the picked card releases its resolving highlight. Safe from any thread.
    /// </summary>
    public void OnStreamResolutionEnded()
    {
        _resolvingGame = null;
        lock (_pendingLock)
        {
            _pendingClearResolving = true;
        }
    }

    private void OnCardFocused(MatchCardViewModel card) => OnFocusPulse();

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
