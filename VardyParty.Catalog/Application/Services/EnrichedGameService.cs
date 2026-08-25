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
    private readonly int _apiTtl = gamesApiSettings.Value?.RefreshSchedule ?? 300;
    private readonly int _bbcTtl = bbcFixturesSettings.Value?.RefreshSchedule ?? 300;
    private readonly BehaviorSubject<string?> _errorSubject = new(null);
    private readonly Dictionary<string, List<Game>> _latestApiGames = new();
    private readonly object _startLock = new();
    private readonly object _stateLock = new();
    private readonly BehaviorSubject<Dictionary<string, List<Game>>?> _subject = new(null);
    private Timer? _apiTimer;
    private Timer? _bbcTimer;
    private int _bbcFetchInFlight;
    private bool _hasFetchedApi;
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

            RunMatching();
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

        try
        {
            logger.LogInformation("[Enriched] Polling BBC fixtures...");
            var fixtures = await bbc.GetRollingWindowFixturesAsync();
            lock (_stateLock)
            {
                _latestBbcFixtures = fixtures;
            }

            RunMatching();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Enriched] Background BBC fetch failed");
        }
        finally
        {
            Interlocked.Exchange(ref _bbcFetchInFlight, 0);
        }
    }

    private void RunMatching()
    {
        Dictionary<string, List<Game>> apiCopy;
        List<BbcFixture> bbcCopy;
        bool ready;

        lock (_stateLock)
        {
            ready = _hasFetchedApi;
            // manual deep copy of structure (lists are refs but we replace them in fetch)
            // Actually, Game objects are refs. 
            // Matcher modifies Game objects in-place. 
            // We should ideally clone key structure.
            apiCopy = _latestApiGames.ToDictionary(k => k.Key, v => v.Value.ToList());
            bbcCopy = _latestBbcFixtures.ToList();
        }

        if (!ready) return;

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