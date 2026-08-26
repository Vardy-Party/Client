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
    ILogger<EnrichedGameService> logger
) : IEnrichedGameService, IDisposable
{
    /// <summary>
    /// How long a coalesced initial API publish waits for the initial BBC fetch
    /// before giving up and publishing the un-enriched board anyway (a hung BBC
    /// endpoint must never hold the whole homepage hostage).
    /// </summary>
    public static readonly TimeSpan InitialPublishGrace = TimeSpan.FromSeconds(3);

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
    private bool _initialApiPublishSkipped;
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
                // publish was coalesced while waiting for these fixtures.
                publish = fetched || (wasInitial && _initialApiPublishSkipped);
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

            // Startup coalescing: the two initial fetches used to complete ~1s
            // apart and produce two near-simultaneous full-board publishes — the
            // second one reset the homepage mid-first-materialization of the
            // nested CollectionViews (WinUI's documented 0x800710DD failure
            // mode). If the API fetch wins the race, skip its standalone publish
            // (at most once, ever) and let the initial BBC completion publish
            // the single enriched board; a grace timer publishes anyway if BBC
            // never comes back. Steady-state live-score updates are unaffected:
            // once the initial BBC fetch has completed (or the one skip is
            // spent), every RunMatching publishes immediately.
            if (fromApiFetch && !_initialBbcCompleted && !_initialApiPublishSkipped)
            {
                _initialApiPublishSkipped = true;
                logger.LogInformation(
                    "[Enriched] Coalescing initial publish: waiting up to {Grace}s for the first BBC fetch",
                    InitialPublishGrace.TotalSeconds);
                SchedulePublishGraceFallback();
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
    /// Armed when the initial API publish is coalesced: if the initial BBC fetch
    /// still hasn't completed after <see cref="InitialPublishGrace"/>, publish
    /// the un-enriched board so the homepage never sits on the loading screen
    /// behind a hung fixtures endpoint. Harmless if BBC completes concurrently
    /// (publishes are serialized; the board content is consistent either way).
    /// </summary>
    private void SchedulePublishGraceFallback()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(InitialPublishGrace);

                bool release;
                lock (_stateLock)
                {
                    release = !_initialBbcCompleted;
                }

                if (release)
                {
                    logger.LogWarning(
                        "[Enriched] Initial BBC fetch still pending after {Grace}s; publishing API-only board",
                        InitialPublishGrace.TotalSeconds);
                    RunMatching(fromApiFetch: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Enriched] Publish grace fallback failed");
            }
        });
    }
}