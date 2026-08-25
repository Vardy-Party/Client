using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Models;
using VardyParty.Parsers;

namespace VardyParty.Services;

public class BbcFixturesService(
    HttpClient http,
    IOptions<BbcFixturesSettings> bbcFixturesSettings,
    ILogger<BbcFixturesService> logger,
    IBbcHtmlParser bbcHtmlParser
) : IBbcFixturesService
{
    private const string FixturesUrl = "https://www.bbc.com/sport/football/scores-fixtures";
    private readonly TimeSpan _callTimeout = TimeSpan.FromSeconds(bbcFixturesSettings.Value?.CallTimeoutSeconds ?? 30);
    private readonly int _maxRetries = bbcFixturesSettings.Value?.MaxRetries ?? 3;

    // HTML parse is CPU-bound; parallel day parses on Android TV thrash a weak core and inflate wall-clock to ~80s+.
    private static readonly SemaphoreSlim ParseGate = new(1, 1);

    public Task<List<BbcFixture>> GetRollingWindowFixturesAsync(CancellationToken cancellationToken = default)
    {
        var pageDates = BbcFixtureSchedule.GetRollingWindowPageDates(DateTime.UtcNow);
        return GetFixturesForDatesAsync(pageDates, cancellationToken);
    }

    public async Task<List<BbcFixture>> GetFixturesForDatesAsync(
        IReadOnlyList<DateOnly> fixturePageDates,
        CancellationToken cancellationToken = default)
    {
        if (fixturePageDates.Count == 0)
        {
            return [];
        }

        var dates = fixturePageDates.Distinct().ToArray();

        // IO-bound fetches can overlap; CPU-bound parses are serialized via ParseGate inside GetFixturesAsync.
        var fetchTasks = dates
            .Select(date => GetFixturesAsync(date, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(fetchTasks);
        return MergeFixtures(results);
    }

    public async Task<List<BbcFixture>> GetFixturesAsync(DateOnly fixturePageDate, CancellationToken cancellationToken = default)
    {
        var url = $"{FixturesUrl}/{fixturePageDate:yyyy-MM-dd}";

        var attempt = 0;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            attempt++;
            try
            {
                string html;
                using (var httpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    httpCts.CancelAfter(_callTimeout);
                    var swHttp = Stopwatch.StartNew();
                    html = await http.GetStringAsync(url, httpCts.Token);
                    swHttp.Stop();
                    logger.LogInformation("[BBC] HTTP fetch for {Date} took {Elapsed}ms. Parse start.",
                        fixturePageDate, swHttp.ElapsedMilliseconds);
                }

                // Parse outside the HTTP timeout so a large match day is not cancelled mid-scan at 20s.
                await ParseGate.WaitAsync(cancellationToken);
                try
                {
                    return await Task.Run(() => bbcHtmlParser.ParseHtml(html, cancellationToken), cancellationToken);
                }
                finally
                {
                    ParseGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[BBC] Operation cancelled / timed out for {Date}", fixturePageDate);
                return [];
            }
            catch (Exception ex) when (attempt <= _maxRetries)
            {
                logger.LogWarning(ex, "[BBC] GetFixtures attempt {Attempt} failed, retrying after {Delay}s", attempt,
                    delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch
                {
                }

                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BBC fixtures fetch failed after {Attempt} attempts", attempt);
                return [];
            }
        }
    }

    private static List<BbcFixture> MergeFixtures(IEnumerable<List<BbcFixture>> fixtureLists)
    {
        var map = new Dictionary<string, BbcFixture>(StringComparer.OrdinalIgnoreCase);

        foreach (var fixture in fixtureLists.SelectMany(static list => list))
        {
            var key = GameMatcher.BuildFixtureKey(fixture.Home, fixture.Away);
            if (!map.TryGetValue(key, out var existing) || PreferFixture(fixture, existing))
            {
                map[key] = fixture;
            }
        }

        return map.Values.ToList();
    }

    private static bool PreferFixture(BbcFixture candidate, BbcFixture incumbent)
    {
        if (candidate.KickoffUtc != DateTime.MinValue && incumbent.KickoffUtc == DateTime.MinValue)
        {
            return true;
        }

        if (candidate.HasProgress && !incumbent.HasProgress)
        {
            return true;
        }

        return false;
    }
}
