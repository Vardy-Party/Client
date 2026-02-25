using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VardyParty.Parsers;
using Xunit;
using Xunit.Abstractions;

namespace VardyParty.Core.Tests;

public class BbcParserPerformanceTests(ITestOutputHelper output)
{
    private static readonly int Timeout = 200;

    [Fact]
    public async Task Parse_RealBbcPage_Performance()
    {
        // url for BBC scores
        var url = "https://www.bbc.com/sport/football/scores-fixtures";

        output.WriteLine($"Downloading real HTML from {url}...");
        var http = new HttpClient();
        // User-Agent to ensure we get the full desktop/mobile site and not a blocked request
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var swTotal = Stopwatch.StartNew();
        string html;
        try
        {
            html = await http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to download: {ex.Message}");
            return; // Skip if network fails
        }

        swTotal.Stop();
        output.WriteLine($"Download complete. Size: {html.Length / 1024} KB. Time: {swTotal.ElapsedMilliseconds}ms");

        // Setup Logger that writes to xUnit output
        var logger = new XunitLogger<BbcHtmlParser>(output);
        var jsonLogger = new XunitLogger<BbcJsonParser>(output);
        var parser = new BbcJsonParser(jsonLogger);
        var htmlParser = new BbcHtmlParser(logger, parser);

        output.WriteLine("Starting ParseHtml...");

        // Warmup (optional)
        // htmlParser.ParseHtml(html);

        var swParse = Stopwatch.StartNew();
        var fixtures = htmlParser.ParseHtml(html);
        swParse.Stop();

        output.WriteLine("--------------------------------------------------");
        output.WriteLine($"Parse Result: {fixtures.Count} fixtures found.");
        output.WriteLine($"Parse Time:   {swParse.ElapsedMilliseconds} ms");
        output.WriteLine("--------------------------------------------------");

        // Sanity assertions
        Assert.NotNull(fixtures);

        if (fixtures.Count > 0)
            Assert.True(swParse.ElapsedMilliseconds < Timeout, $"Parser took too long: {swParse.ElapsedMilliseconds}ms");
    }

    // Simple Logger for Test Output
    private class XunitLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}