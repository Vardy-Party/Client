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

    public async Task<List<BbcFixture>> GetFixturesAsync(DateTime dateUtc)
    {
        var date = dateUtc.ToUniversalTime().Date;
        var url = $"{FixturesUrl}/{date:yyyy-MM-dd}";

        var attempt = 0;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(_callTimeout);
                var swHttp = Stopwatch.StartNew();
                var html = await http.GetStringAsync(url, cts.Token);
                swHttp.Stop();
                logger.LogInformation("[BBC] HTTP fetch took {Elapsed}ms. Parse start.", swHttp.ElapsedMilliseconds);

                // parse off-thread to avoid UI blocking
                return await Task.Run(() => bbcHtmlParser.ParseHtml(html, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[BBC] Operation cancelled / timed out for {Date}", date);
                return new List<BbcFixture>();
            }
            catch (Exception ex) when (attempt <= _maxRetries)
            {
                logger.LogWarning(ex, "[BBC] GetFixtures attempt {Attempt} failed, retrying after {Delay}s", attempt,
                    delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay);
                }
                catch
                {
                }

                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BBC fixtures fetch failed after {Attempt} attempts", attempt);
                return new List<BbcFixture>();
            }
        }
    }
}