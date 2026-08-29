using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;

namespace VardyParty.Catalog;

public class EnrichedGameService(
    IGamesCatalogApi api,
    IBbcFixturesService bbc,
    IGameMatcher matcher,
    IOptions<BbcFixturesSettings> bbcFixturesSettings,
    IOptions<GamesApiSettings> gamesApiSettings,
    ILogger<EnrichedGameService> logger,
    TimeSpan? initialEnrichmentValve = null
) : IEnrichedGameService, IDisposable
{
    /// <summary>
    /// The FIRST board the UI sees must be the ENRICHED one: API games with
    /// BBC scores/minutes matched in. On the field TV the BBC parse outlasted
    /// the old 3s grace, so users saw an API-only board (games without
    /// scores) that then reshuffled when enrichment landed. Every API-driven
    /// publish is therefore held until the initial BBC fetch completes; this
    /// valve — measured FROM POLLING START — releases the un-enriched board
    /// as a fallback if BBC hangs (a dead fixtures endpoint must never hold
    /// the homepage hostage). An initial BBC FAILURE releases immediately.
    /// sized at 30s: long enough to cover a typical TV BBC multi-day parse
    /// burst without the old 10s leak of scoreless games that then reshuffled,
    /// short enough that a hung fixtures endpoint does not strand the board.
    /// Per-page HTTP still fails via CallTimeout and releases on exception;
    /// this only covers a stuck-without-throwing fetch.
    /// </summary>
    public static readonly TimeSpan InitialEnrichmentValve = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _initialValve = initialEnrichmentValve ?? InitialEnrichmentValve;
    private readonly int _apiTtl = gamesApiSettings.Value?.RefreshSchedule ?? 300;
    private readonly int _bbcTtl = bbcFixturesSettings.Value?.RefreshSchedule ?? 300;
    private readonly BehaviorSubject<string?> _errorSubject = new(null);
    private readonly Dictionary<string, List<Game>> _latestApiGames = new();
    private readonly object _startLock = new();
    private readonly object _stateLock = new();
    private readonly object _publishLock = new();
    private readonly BehaviorSubject<Dictionary<string, List<Game>>?> _subject = new(null);
    private Timer? _apiTimer;
    private Timer? _bbcTimer;
    private int _bbcFetchInFlight;
    private bool _hasFetchedApi;
    private bool _initialBbcCompleted;
    private bool _initialApiPublishHeld;
    private bool _initialValveExpired;
    private List<BbcFixture> _latestBbcFixtures = new();
    private bool _timersStarted;

    public void Dispose()
    {
        _apiTimer?.Dispose();
        _bbcTimer?.Dispose();
        _subject?.Dispose();
        _errorSubject?.Dispose();
    }

    public IObservable<Dictionary<string, List<Game>>?> GamesStream => _subject.AsObservable();
    public IObservable<string?> ErrorStream => _errorSubject.AsObservable();

    public Dictionary<string, List<Game>>? GetLatestGames() => _subject.Value;

    public void StartBackgroundPolling()
    {
        Task.Run(async () =>
        {
            try
            {
                lock (_startLock)
                {
                    if (_timersStarted) return;

                    logger.LogInformation("[Enriched] Starting background pollers. API TTL={Api}s, BBC TTL={Bbc}s",
                        _apiTtl, _bbcTtl);

                    // Initial fetch immediately
                    _ = FetchApiGames();
                    _ = FetchBbcFixtures();

                    _apiTimer = new Timer(_ => _ = FetchApiGames(), null, _apiTtl * 1000, _apiTtl * 1000);
                    _bbcTimer = new Timer(_ => _ = FetchBbcFixtures(), null, _bbcTtl * 1000, _bbcTtl * 1000);

                    _timersStarted = true;
                    ScheduleInitialEnrichmentValve();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Enriched] Failed to start background polling");
            }
        });
    }

    private async Task FetchApiGames()
    {
        try
        {
            logger.LogInformation("[Enriched] Polling API games...");
            var games = await api.GetAllGamesAsync(true);

            // Clear any previous error on successful fetch
            _errorSubject.OnNext(null);

            lock (_stateLock)
            {
                _latestApiGames.Clear();
                foreach (var kvp in games) _latestApiGames[kvp.Key] = kvp.Value;
                _hasFetchedApi = true;
            }

            RunMatching(fromApiFetch: true);
        }
        catch (ApiSystemDownException ex)
        {
            logger.LogError(ex, "[Enriched] API system is down");
            _errorSubject.OnNext("The system is down right now. Try again later");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Enriched] Background API fetch failed");
        }
    }

    private async Task FetchBbcFixtures()
    {
        if (Interlocked.CompareExchange(ref _bbcFetchInFlight, 1, 0) != 0)
        {
            logger.LogInformation("[Enriched] Skipping BBC poll; previous fetch still in progress");
            return;
        }

        var fetched = false;
        try
        {
            logger.LogInformation("[Enriched] Polling BBC fixtures...");
            var fixtures = await bbc.GetRollingWindowFixturesAsync();
            lock (_stateLock)
            {
                _latestBbcFixtures = fixtures;
            }

            fetched = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Enriched] Background BBC fetch failed");
        }
        finally
        {
            bool publish;
            lock (_stateLock)
            {
                var wasInitial = !_initialBbcCompleted;
                _initialBbcCompleted = true;

                // Publish on success (new fixtures), or — when the very first
                // BBC fetch failed — to release an initial API board whose
                // publish was held for enrichment (the failure valve: a down
                // fixtures endpoint releases the API-only board immediately).
                publish = fetched || (wasInitial && _initialApiPublishHeld);
            }

            if (publish) RunMatching(fromApiFetch: false);
            Interlocked.Exchange(ref _bbcFetchInFlight, 0);
        }
    }

    private void RunMatching(bool fromApiFetch)
    {
        Dictionary<string, List<Game>> apiCopy;
        List<BbcFixture> bbcCopy;

        lock (_stateLock)
        {
            if (!_hasFetchedApi) return;

            // Enriched-first initial publish: the first board the UI sees must
            // have BBC enrichment matched in (user decision — an API-only
            // board rendered games without scores that then reshuffled when
            // enrichment landed; it also caused WinUI's 0x800710DD double
            // full-board reset mid-first-materialization). EVERY API-driven
            // publish is held while the initial BBC fetch is still in flight;
            // the initial BBC completion (success OR failure) publishes the
            // single initial board, and the enrichment valve (armed at
            // polling start) releases the un-enriched board if BBC hangs.
            // Steady state is unaffected: once the initial BBC fetch has
            // completed or the valve has expired, publishes are immediate.
            if (fromApiFetch && !_initialBbcCompleted && !_initialValveExpired)
            {
                _initialApiPublishHeld = true;
                logger.LogInformation(
                    "[Enriched] Holding initial publish for BBC enrichment (valve {Valve}s from polling start)",
                    _initialValve.TotalSeconds);
                return;
            }

            // manual deep copy of structure (lists are refs but we replace them in fetch)
            // Actually, Game objects are refs. 
            // Matcher modifies Game objects in-place. 
            // We should ideally clone key structure.
            apiCopy = _latestApiGames.ToDictionary(k => k.Key, v => v.Value.ToList());
            bbcCopy = _latestBbcFixtures.ToList();
        }

        // Serialize match+publish: the API and BBC pollers run concurrently, and
        // an unserialized _subject.OnNext lets GamesStream emit from two threads
        // at once (observers like HomeViewModel.Rebuild are not written for
        // that). BehaviorSubject also makes no cross-thread OnNext guarantees.
        lock (_publishLock)
        {
            try
            {
                var flatGames = apiCopy.Values.SelectMany(x => x).ToList();
                if (flatGames.Count > 0) matcher.EnrichGames(flatGames, bbcCopy, "(background)");

                // Push raw enriched API data update (do not mutate structure here)
                _subject.OnNext(apiCopy);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Enriched] Matching failed");
            }
        }
    }

    /// <summary>
    /// Armed once at polling start: if the initial BBC fetch still hasn't
    /// completed after <see cref="InitialEnrichmentValve"/>, release the held
    /// API-only board so the homepage never sits on the loading screen behind
    /// a hung fixtures endpoint. Harmless if BBC completes concurrently
    /// (publishes are serialized; the board content is consistent either way).
    /// </summary>
    private void ScheduleInitialEnrichmentValve()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_initialValve);

                bool release;
                lock (_stateLock)
                {
                    _initialValveExpired = true;
                    release = !_initialBbcCompleted && _hasFetchedApi;
                }

                if (release)
                {
                    logger.LogWarning(
                        "[Enriched] Initial BBC fetch still pending after {Valve}s; releasing the API-only board",
                        _initialValve.TotalSeconds);
                    RunMatching(fromApiFetch: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Enriched] Initial enrichment valve failed");
            }
        });
    }
}