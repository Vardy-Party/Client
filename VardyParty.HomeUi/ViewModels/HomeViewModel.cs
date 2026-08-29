using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
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
    private readonly MatchEventNotificationPolicy _notifications;
    private readonly MatchEventBus _events;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly MatchEventDetector _matchEvents = new();
    private readonly object _pendingLock = new();
    private readonly Queue<Action> _pendingUiAssign = new();
    private readonly Queue<StagedStrip> _stagedStrips = new();
    private int _imageEpoch;
    private IDictionary<string, List<Game>>? _lastGames;
    private HashSet<string> _lastLiveLeagues = new(StringComparer.OrdinalIgnoreCase);
    private string? _focusedLeague;
    private bool _isMenuOpen;
    private string _subtitle = LoadingSubtitle;
    private string _errorMessage = string.Empty;
    private bool _hasGames;
    private bool _isContentLoading = true;
    private Game? _resolvingGame;
    private PendingApply? _pendingApply;
    private string? _pendingError;
    private bool _pendingClearResolving;
    private bool _pendingResetScores;

    /// <summary>
    /// Header subtitle until the first catalog WITH API data lands (the games
    /// feed is a BehaviorSubject seeded with null, so subscribing delivers a
    /// null board immediately — that must read as loading, never "0 games").
    /// Pairs with the spinning crest: only null (pre-API seed / sign-out)
    /// keeps both going; any delivered board — even a legitimately empty one —
    /// is ready.
    /// </summary>
    public const string LoadingSubtitle = "Loading…";

    private sealed record PendingApply(
        IReadOnlyList<LeagueRowModel> Rows,
        IReadOnlyList<Game> Display,
        IDictionary<string, List<Game>>? Dict);

    /// <summary>Cards a new row still owes its strip (staged materialization).</summary>
    private sealed class StagedStrip
    {
        public required LeagueRowViewModel Row { get; init; }
        public required IReadOnlyList<Game> Games { get; init; }
        public required int Epoch { get; init; }
        public int Next { get; set; }
    }

    public HomeViewModel(
        ILeagueFilterService leagueFilter,
        MenuViewModel menu,
        IBadgeImageLoader images,
        IHomeAssetLocator assets,
        UiSoundService sounds,
        MatchEventNotificationPolicy notifications,
        MatchEventBus events,
        ILogger<HomeViewModel> logger)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Toast = new MatchEventToastViewModel(Layout);

        _leagueFilter.Changed += OnFilterChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when the user picks a match.</summary>
    public event Action<Game>? GamePicked;

    /// <summary>Raised when the user taps "Sign out" in the settings menu (head-handled).</summary>
    public event Action? SignOutRequested;

    /// <summary>Raised on the UI thread after rows are rebuilt, with the visible game count.</summary>
    public event Action<int>? GamesUpdated;

    /// <summary>
    /// Raised when catalog/error/image work is queued from any thread.
    /// <see cref="Views.HomeView"/> starts the UI apply pump from this.
    /// </summary>
    public event Action? WorkQueued;

    /// <summary>
    /// Gets a value that indicates whether the UI-thread apply pump still has
    /// catalog, error, or image work to drain.
    /// </summary>
    public bool HasPendingWork
    {
        get
        {
            lock (_pendingLock)
            {
                return _pendingApply != null
                    || _pendingError != null
                    || _pendingClearResolving
                    || _pendingResetScores
                    || _pendingUiAssign.Count > 0;
            }
        }
    }

    public HomeLayoutState Layout { get; } = new();

    /// <summary>The homepage match-event toast (queue/dismiss state machine).</summary>
    public MatchEventToastViewModel Toast { get; }

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
    /// Empty copy only after a board WITH API data has arrived (all leagues
    /// filtered out by the user). Null and empty boards are still loading —
    /// the enriched-first service never delivers an empty initial board as a
    /// settled state.
    /// </summary>
    public bool ShowEmptyState => !_hasGames && !_isContentLoading;

    /// <summary>
    /// Keep the rows host hidden until the enriched-first service publishes
    /// (BBC done or valve). Painting cards while <see cref="IsContentLoading"/>
    /// is true showed scoreless games that then reshuffled, and left the TV
    /// scroller mid-board. Desktop/Windows bind the same property — rows stay
    /// empty until the first board anyway, so the gate is effectively a no-op.
    /// </summary>
    public bool ShowGameRows => !_isContentLoading;

    /// <summary>
    /// True until a games dictionary WITH API data lands (and again after a
    /// sign-out clears the feed). This is the crest's spin signal: it reflects
    /// actual catalog readiness, never a timer — only null applies (pre-API
    /// seed / sign-out) keep it loading; a delivered board, empty included,
    /// is ready.
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
            Raise(nameof(ShowGameRows));
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

        NotifyWorkQueued();
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

    /// <summary>
    /// Settings: the persisted "Goal notifications" switch (default ON).
    /// OFF suppresses the sting, the toast AND the card flash; the separate
    /// "UI sounds" switch still gates the sting's audio when this is ON.
    /// </summary>
    public bool GoalNotificationsEnabled
    {
        get => _menu.GoalNotificationsEnabled;
        set
        {
            if (_menu.GoalNotificationsEnabled == value) return;
            _menu.ToggleGoalNotifications();
            Raise(nameof(GoalNotificationsEnabled));
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

        NotifyWorkQueued();
    }

    public void Dispose() => _leagueFilter.Changed -= OnFilterChanged;

    private void OnFilterChanged() => Rebuild();

    private void Rebuild()
    {
        var dict = _lastGames;

        // Filter/group off the caller thread. WinAppSDK 1.8 stows a
        // 0xc000027b if we Dispatcher.Dispatch from the Rx/HTTP thread into
        // WinUI layout; HomeView drains this queue on the UI thread (timer on
        // Windows, MainThread on Android/Desktop).
        var display = dict == null
            ? new List<Game>()
            : _leagueFilter.FilterGames(dict.ToDisplay());
        var rowModels = HomeRowsBuilder.Build(display);

        lock (_pendingLock)
        {
            _pendingApply = new PendingApply(rowModels, display, dict);
        }

        NotifyWorkQueued();
    }

    /// <summary>
    /// Drain catalog/error/image work that arrived off the UI thread. Must run
    /// on the UI thread (HomeView starts the drain; hosts only call UpdateGames).
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

        try
        {
            if (resetScores)
            {
                _matchEvents.Reset();
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
                // The detector sees the FILTERED display list, so games in
                // hidden leagues are never observed and never fire. Delivery
                // (sting/toast/flash vs nothing) is the policy's call:
                // backgrounded events are dropped entirely, never queued.
                var events = _matchEvents.Observe(apply.Display);
                Apply(apply.Rows, apply.Display.Count, apply.Dict);
                DeliverMatchEvents(events);
            }

            DrainPendingImageAssigns();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Homepage UI apply failed");
        }
    }

    /// <summary>
    /// Deliver detected events AFTER the board applied, so the row/card VMs
    /// the toast borrows (league icon, badges) already carry the new state.
    /// <see cref="MatchEventNotificationPolicy.ShouldPresent"/> gates
    /// everything — backgrounded or toggled-off events are dropped here,
    /// never queued for a catch-up. Audio additionally requires the homepage
    /// to be the active surface (no stream playing): one sting per apply,
    /// however many events it carried, routed through UiSoundService which
    /// adds the "UI sounds" toggle + playback suppression on top.
    /// </summary>
    private void DeliverMatchEvents(IReadOnlyList<MatchEvent> events)
    {
        if (events.Count == 0 || !_notifications.ShouldPresent)
        {
            return;
        }

        if (_notifications.ShouldPlayAudio)
        {
            _sounds.Play(UiSound.Goal);
        }

        foreach (var matchEvent in events)
        {
            // Delivered-event stream: the homepage toast consumes it below;
            // the playback overlays (Desktop panel, Android banner, Windows
            // player grid) subscribe to the same bus.
            _events.Publish(matchEvent);

            Toast.Publish(BuildToastItem(matchEvent));

            // Synchronized card flash — only when the card is materialized
            // (a staged strip's unmaterialized tail simply has no card).
            FindCardForEvent(matchEvent)?.RequestFlash();
        }
    }

    /// <summary>
    /// One toast payload for a delivered event, borrowing the league icon and
    /// team badges from whatever the board already has materialized (the
    /// shared badge cache's ImageSources — a staged/hidden card simply has
    /// none and the monogram initials cover it). The homepage toast uses this
    /// directly; the in-playback overlays call it from their bus subscribers
    /// so every surface renders the SAME league logo + both badges.
    /// UI thread only (walks the live row/card collections).
    /// </summary>
    public MatchEventToastItem BuildToastItem(MatchEvent matchEvent)
    {
        var row = FindRowForEvent(matchEvent);
        var card = FindCardForEvent(matchEvent);
        return new MatchEventToastItem(
            matchEvent, row?.LeagueIcon, card?.HomeBadge, card?.AwayBadge);
    }

    private LeagueRowViewModel? FindRowForEvent(MatchEvent matchEvent)
    {
        var league = MatchEventPresenter.LeagueName(matchEvent);
        return Rows.FirstOrDefault(r =>
            string.Equals(r.League, league, StringComparison.OrdinalIgnoreCase));
    }

    private MatchCardViewModel? FindCardForEvent(MatchEvent matchEvent)
    {
        var key = HomeBoardDiffer.GameKey(matchEvent.Game);
        return EnumerateCards().FirstOrDefault(c =>
            string.Equals(HomeBoardDiffer.GameKey(c.Game), key, StringComparison.Ordinal));
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

        var epoch = Interlocked.Increment(ref _imageEpoch);

        // Flip loading before materializing rows so ShowGameRows opens on the
        // same apply as the first enriched (or valve) board — never paint
        // cards under a still-true IsContentLoading (TV scroller / autofocus
        // then sit on a board that is about to reshuffle).
        var hasApiData = dict != null;
        IsContentLoading = !hasApiData;
        HasGames = gameCount > 0;
        var liveCount = rowModels.Sum(r => r.Games.Count(g => g.IsLiveForOrdering));
        Subtitle = hasApiData ? FormatSubtitle(gameCount, liveCount) : LoadingSubtitle;

        ApplyRowsDiff(rowModels);

        if (!hadGames && Rows.Count > 0 && Rows[0].Cards.Count > 0)
        {
            Rows[0].Cards[0].RequestsInitialFocus = true;
        }

        GameCount = gameCount;

        GamesUpdated?.Invoke(gameCount);

        _ = LoadImagesAsync(BuildImagePlan(), epoch);
    }

    /// <summary>
    /// In-place board update (replaces the old Clear+rebuild-per-poll):
    /// <see cref="HomeBoardDiffer"/> plans a sticky ordering — rows keep their
    /// positions except on live-set transitions, the focused row never moves,
    /// card order inside a row is stable — and this method mutates the
    /// ObservableCollections to match: existing card VMs update their INPC
    /// properties in place, only real additions/removals touch the
    /// collections. Materialized card views (and loaded badges/icons) survive
    /// every poll.
    /// </summary>
    private void ApplyRowsDiff(IReadOnlyList<LeagueRowModel> rowModels)
    {
        var planned = HomeBoardDiffer.PlanRowOrder(
            Rows.Select(r => r.League).ToList(),
            rowModels,
            _lastLiveLeagues,
            _focusedLeague);

        var plannedLeagues = new HashSet<string>(
            planned.Select(r => r.League), StringComparer.OrdinalIgnoreCase);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!plannedLeagues.Contains(Rows[i].League))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < planned.Count; i++)
        {
            var model = planned[i];
            var existingIdx = IndexOfRow(model.League, startAt: i);
            if (existingIdx < 0)
            {
                Rows.Insert(i, CreateRow(model));
                continue;
            }

            if (existingIdx != i)
            {
                // Re-tier move (rare: live-set transitions only, never the
                // focused row). Remove+Insert rather than Move: WinUI's
                // CollectionView Move handling is the platform this repo has
                // scar tissue with, and moved rows re-materialize anyway.
                var row = Rows[existingIdx];
                Rows.RemoveAt(existingIdx);
                Rows.Insert(i, row);
            }

            UpdateRowInPlace(Rows[i], model);
        }

        _lastLiveLeagues = new HashSet<string>(
            rowModels.Where(r => r.HasLiveGames).Select(r => r.League),
            StringComparer.OrdinalIgnoreCase);
    }

    private int IndexOfRow(string league, int startAt)
    {
        for (var i = startAt; i < Rows.Count; i++)
        {
            if (string.Equals(Rows[i].League, league, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Strip staging (<see cref="HomeLayoutState.StagedStripCards"/>, TV
    /// only): card strips are BindableLayouts, so a NEW row materializes
    /// every card the moment it binds — a 15-card cup row is one huge layout
    /// pass on the weak TV core. Over the budget, the row starts with its
    /// first N cards and owes the rest to <see cref="MaterializeNextStagedStripChunk"/>,
    /// which the view pumps in dispatcher-idle chunks. Existing rows are
    /// exempt: in-place diffs insert a handful of cards at most.
    /// </summary>
    private LeagueRowViewModel CreateRow(LeagueRowModel model)
    {
        var budget = Layout.StagedStripCards;
        if (budget <= 0 || model.Games.Count <= budget)
        {
            return new(model.League, model.HasLiveGames, model.Games.Select(CreateCard).ToList(), Layout);
        }

        var row = new LeagueRowViewModel(
            model.League,
            model.HasLiveGames,
            model.Games.Take(budget).Select(CreateCard).ToList(),
            Layout);
        _stagedStrips.Enqueue(new StagedStrip
        {
            Row = row,
            Games = model.Games,
            Epoch = _imageEpoch,
            Next = budget,
        });
        return row;
    }

    /// <summary>How many staged cards one dispatcher-idle pump tick appends.</summary>
    public const int StagedStripChunkSize = 4;

    /// <summary>Whether any row still owes staged cards to its strip. UI thread only.</summary>
    public bool HasStagedStripWork
    {
        get
        {
            PruneStaleStagedStrips();
            return _stagedStrips.Count > 0;
        }
    }

    private bool _stagedAppendsPaused;

    /// <summary>
    /// Appends yield to interaction: while a strip ScrollView is being
    /// dragged/scrolled, chunk appends would land mid-gesture and hitch the
    /// touch drag (phone field report). The view pauses on scroll events and
    /// resumes (re-kicking the pump) once the scroll goes quiet. UI thread only.
    /// </summary>
    public void PauseStagedStripAppends() => _stagedAppendsPaused = true;

    /// <summary>Scroll idle again: appends may continue (the view re-kicks the pump).</summary>
    public void ResumeStagedStripAppends() => _stagedAppendsPaused = false;

    /// <summary>
    /// Append the next chunk of staged cards (UI thread only; the view posts
    /// one call per dispatcher message so frames and D-pad input interleave).
    /// Returns true while more chunks remain. A newer apply supersedes staged
    /// work: its diff plans against the full board, so stale entries are
    /// simply dropped. While appends are paused (strip scroll in flight) this
    /// refuses without appending — the view resumes the pump on idle.
    /// </summary>
    public bool MaterializeNextStagedStripChunk()
    {
        PruneStaleStagedStrips();
        if (_stagedAppendsPaused || _stagedStrips.Count == 0)
        {
            return false;
        }

        var entry = _stagedStrips.Peek();
        var end = Math.Min(entry.Next + StagedStripChunkSize, entry.Games.Count);
        for (var i = entry.Next; i < end; i++)
        {
            entry.Row.Cards.Add(CreateCard(entry.Games[i]));
        }

        entry.Next = end;
        if (entry.Next >= entry.Games.Count)
        {
            _stagedStrips.Dequeue();
        }

        // Newly materialized cards need their badges; the plan only contains
        // still-missing images so this is cheap and idempotent.
        _ = LoadImagesAsync(BuildImagePlan(), Volatile.Read(ref _imageEpoch));

        PruneStaleStagedStrips();
        return _stagedStrips.Count > 0;
    }

    private void PruneStaleStagedStrips()
    {
        while (_stagedStrips.Count > 0 && _stagedStrips.Peek().Epoch != Volatile.Read(ref _imageEpoch))
        {
            _stagedStrips.Dequeue();
        }
    }

    private MatchCardViewModel CreateCard(Game game) =>
        new(game, Layout, OnCardPicked, OnCardFocused)
        {
            IsResolving = HomePlaybackIntent.SameGame(_resolvingGame, game),
        };

    private void UpdateRowInPlace(LeagueRowViewModel row, LeagueRowModel model)
    {
        var planned = HomeBoardDiffer.PlanCardOrder(
            row.Cards.Select(c => HomeBoardDiffer.GameKey(c.Game)).ToList(),
            model.Games);

        var plannedKeys = new HashSet<string>(
            planned.Select(HomeBoardDiffer.GameKey), StringComparer.Ordinal);
        for (var i = row.Cards.Count - 1; i >= 0; i--)
        {
            if (!plannedKeys.Contains(HomeBoardDiffer.GameKey(row.Cards[i].Game)))
            {
                row.Cards.RemoveAt(i);
            }
        }

        for (var i = 0; i < planned.Count; i++)
        {
            var game = planned[i];
            var key = HomeBoardDiffer.GameKey(game);
            var existingIdx = IndexOfCard(row, key, startAt: i);
            if (existingIdx < 0)
            {
                row.Cards.Insert(i, CreateCard(game));
                continue;
            }

            if (existingIdx != i)
            {
                var card = row.Cards[existingIdx];
                row.Cards.RemoveAt(existingIdx);
                row.Cards.Insert(i, card);
            }

            row.Cards[i].UpdateFrom(game);
            row.Cards[i].IsResolving = HomePlaybackIntent.SameGame(_resolvingGame, game);
        }

        row.Refresh(model.HasLiveGames);
    }

    private static int IndexOfCard(LeagueRowViewModel row, string key, int startAt)
    {
        for (var i = startAt; i < row.Cards.Count; i++)
        {
            if (string.Equals(HomeBoardDiffer.GameKey(row.Cards[i].Game), key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void EnqueueUiAssign(Action assign)
    {
        lock (_pendingLock)
        {
            _pendingUiAssign.Enqueue(assign);
        }

        NotifyWorkQueued();
    }

    private void DrainPendingImageAssigns()
    {
        while (true)
        {
            Action? assign;
            lock (_pendingLock)
            {
                if (_pendingUiAssign.Count == 0) return;
                assign = _pendingUiAssign.Dequeue();
            }

            try
            {
                assign();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Homepage image assign failed");
            }
        }
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

    private sealed record RowImagePlan(
        LeagueRowViewModel Row,
        Game? IconGame,
        IReadOnlyList<CardImagePlan> Cards);

    private sealed record CardImagePlan(
        MatchCardViewModel Card,
        string? HomeBadgeUrl,
        string? AwayBadgeUrl);

    /// <summary>
    /// Snapshot of the image work still missing, taken ON the UI thread: the
    /// row/card collections are mutated in place by later applies, so the
    /// background loader must never iterate them. With in-place diff updates
    /// most refreshes produce an empty plan — icons and badges survive on the
    /// long-lived VMs (no per-poll re-decode; the loader's URL-keyed cache is
    /// only consulted for genuinely new fixtures).
    /// </summary>
    private IReadOnlyList<RowImagePlan> BuildImagePlan()
    {
        var plan = new List<RowImagePlan>(Rows.Count);
        foreach (var row in Rows)
        {
            var cards = new List<CardImagePlan>();
            foreach (var card in row.Cards)
            {
                var homeUrl = card.HomeBadge == null ? card.Game.HomeBadgeUrl : null;
                var awayUrl = card.AwayBadge == null ? card.Game.AwayBadgeUrl : null;
                if (homeUrl != null || awayUrl != null)
                {
                    cards.Add(new CardImagePlan(card, homeUrl, awayUrl));
                }
            }

            var iconGame = row.LeagueIcon == null ? row.Cards.FirstOrDefault()?.Game : null;
            if (iconGame != null || cards.Count > 0)
            {
                plan.Add(new RowImagePlan(row, iconGame, cards));
            }
        }

        return plan;
    }

    private async Task LoadImagesAsync(IReadOnlyList<RowImagePlan> plan, int epoch)
    {
        foreach (var rowPlan in plan)
        {
            if (Volatile.Read(ref _imageEpoch) != epoch)
            {
                return;
            }

            if (rowPlan.IconGame != null)
            {
                var iconPath = await _assets.ResolveLeagueLogoPathAsync(rowPlan.IconGame).ConfigureAwait(false);
                var icon = await _images.LoadLocalAsync(iconPath).ConfigureAwait(false);
                if (icon != null && Volatile.Read(ref _imageEpoch) == epoch)
                {
                    EnqueueUiAssign(() => rowPlan.Row.LeagueIcon = icon);
                }
            }

            foreach (var cardPlan in rowPlan.Cards)
            {
                if (Volatile.Read(ref _imageEpoch) != epoch)
                {
                    return;
                }

                var home = await _images.LoadRemoteAsync(cardPlan.HomeBadgeUrl).ConfigureAwait(false);
                var away = await _images.LoadRemoteAsync(cardPlan.AwayBadgeUrl).ConfigureAwait(false);
                if (home == null && away == null) continue;
                if (Volatile.Read(ref _imageEpoch) != epoch)
                {
                    return;
                }

                EnqueueUiAssign(() =>
                {
                    if (home != null) cardPlan.Card.HomeBadge = home;
                    if (away != null) cardPlan.Card.AwayBadge = away;
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

        NotifyWorkQueued();
    }

    /// <summary>
    /// Focus tracking feeds the differ's focus-row protection: the row holding
    /// the focused card never moves on refresh (not even on live-set
    /// re-tiers). UI-thread only, like all focus callbacks.
    /// </summary>
    private void OnCardFocused(MatchCardViewModel card)
    {
        _focusedLeague = Rows.FirstOrDefault(r => r.Cards.Contains(card))?.League ?? _focusedLeague;
        OnFocusPulse();
    }

    private void NotifyWorkQueued() => WorkQueued?.Invoke();

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
