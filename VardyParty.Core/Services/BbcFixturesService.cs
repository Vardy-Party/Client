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

        var fetchTasks = fixturePageDates
            .Distinct()
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
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_callTimeout);
                var swHttp = Stopwatch.StartNew();
                var html = await http.GetStringAsync(url, cts.Token);
                swHttp.Stop();
                logger.LogInformation("[BBC] HTTP fetch for {Date} took {Elapsed}ms. Parse start.",
                    fixturePageDate, swHttp.ElapsedMilliseconds);

                return await Task.Run(() => bbcHtmlParser.ParseHtml(html, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
