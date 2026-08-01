using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.Parsers;
using Xunit;
using Xunit.Abstractions;

namespace VardyParty.Core.Tests;

/// <summary>
/// Baseline: captured BBC scores-fixtures HTML from 2026-08-01 (319 fixtures, ~1.9MB).
/// Use this offline fixture to measure parser improvements and catch regressions.
/// </summary>
public class BbcParserPerformanceTests(ITestOutputHelper output)
{
    // Desktop median is typically under 100ms; CI/shared runners get headroom.
    private const int FixtureBudgetMs = 500;
    private const string BaselineFileName = "BbcScoresFixtures_2026-08-01.html";
    private const int BaselineExactFixtures = 319;
    private const int BaselineExactLeagues = 25;
    private const int BaselineMinWithScores = 180;
    private const int BaselineMinWithKickoffs = 300;

    [Fact]
    public void Parse_BaselineBbcHtml_PerformanceAndCorrectness()
    {
        var html = LoadBaselineHtml();
        output.WriteLine($"Baseline HTML: {html.Length / 1024} KB from {BaselineFileName}");

        var htmlParser = new BbcHtmlParser(
            NullLogger<BbcHtmlParser>.Instance,
            new BbcJsonParser(NullLogger<BbcJsonParser>.Instance));

        // Warmup (JIT / tiered compilation) then take median of measured runs.
        _ = htmlParser.ParseHtml(html);

        var samples = new long[5];
        var fixtures = htmlParser.ParseHtml(html);
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            fixtures = htmlParser.ParseHtml(html);
            sw.Stop();
            samples[i] = sw.ElapsedMilliseconds;
        }

        Array.Sort(samples);
        var medianMs = samples[samples.Length / 2];
        var leagues = fixtures.Select(f => f.League).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().Count();
        var withScores = fixtures.Count(f => f.HomeScore.HasValue || f.AwayScore.HasValue);
        var withKickoffs = fixtures.Count(f => f.KickoffUtc != default && f.KickoffUtc != DateTime.MinValue);

        output.WriteLine("--------------------------------------------------");
        output.WriteLine($"Parse Result: {fixtures.Count} fixtures");
        output.WriteLine($"Samples ms:   {string.Join(", ", samples)}");
        output.WriteLine($"Median ms:    {medianMs}");
        output.WriteLine($"Leagues:      {leagues}");
        output.WriteLine($"With scores:  {withScores}");
        output.WriteLine($"With kickoff: {withKickoffs}");
        output.WriteLine("--------------------------------------------------");

        Assert.Equal(BaselineExactFixtures, fixtures.Count);
        Assert.Equal(BaselineExactLeagues, leagues);
        Assert.True(withScores >= BaselineMinWithScores, $"Expected >= {BaselineMinWithScores} scored fixtures, got {withScores}");
        Assert.True(withKickoffs >= BaselineMinWithKickoffs, $"Expected >= {BaselineMinWithKickoffs} kickoffs, got {withKickoffs}");
        Assert.Contains(fixtures, f => !string.IsNullOrWhiteSpace(f.Home) && !string.IsNullOrWhiteSpace(f.Away));
        Assert.True(medianMs < FixtureBudgetMs,
            $"Baseline median parse {medianMs}ms exceeded budget {FixtureBudgetMs}ms (samples: {string.Join(",", samples)})");
    }

    [Fact]
    public async Task Parse_LiveBbcPage_Performance_Optional()
    {
        // Optional live check — skips on network failure so CI stays deterministic.
        const string url = "https://www.bbc.com/sport/football/scores-fixtures";
        output.WriteLine($"Downloading live HTML from {url}...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(30);

        string html;
        try
        {
            html = await http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Skipping live BBC parse (network): {ex.Message}");
            return;
        }

        output.WriteLine($"Download complete. Size: {html.Length / 1024} KB");

        var htmlParser = new BbcHtmlParser(
            new XunitLogger<BbcHtmlParser>(output),
            new BbcJsonParser(new XunitLogger<BbcJsonParser>(output)));

        var sw = Stopwatch.StartNew();
        var fixtures = htmlParser.ParseHtml(html);
        sw.Stop();

        output.WriteLine($"Live parse: {fixtures.Count} fixtures in {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(fixtures);
        if (fixtures.Count > 0)
            Assert.True(sw.ElapsedMilliseconds < FixtureBudgetMs,
                $"Live parse took too long: {sw.ElapsedMilliseconds}ms");
    }

    private static string LoadBaselineHtml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", BaselineFileName);
        if (!File.Exists(path))
            path = Path.Combine("Resources", BaselineFileName);
        if (!File.Exists(path))
            path = Path.Combine("tests", "VardyParty.Core.Tests", "Resources", BaselineFileName);

        Assert.True(File.Exists(path), $"Missing BBC baseline fixture at {path}");
        return File.ReadAllText(path);
    }

    private class XunitLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
