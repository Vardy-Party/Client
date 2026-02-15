using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VardyParty.Services;
using Moq;
using Xunit;
using Xunit.Abstractions;
using VardyParty.Parsers;

namespace VardyParty.Core.Tests
{
    public class BbcParserPerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public BbcParserPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Parse_RealBbcPage_Performance()
        {
            // url for BBC scores
            var url = "https://www.bbc.com/sport/football/scores-fixtures";
            
            _output.WriteLine($"Downloading real HTML from {url}...");
            var http = new HttpClient();
            // User-Agent to ensure we get the full desktop/mobile site and not a blocked request
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var swTotal = Stopwatch.StartNew();
            string html;
            try 
            {
                html = await http.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Failed to download: {ex.Message}");
                return; // Skip if network fails
            }
            swTotal.Stop();
            _output.WriteLine($"Download complete. Size: {html.Length / 1024} KB. Time: {swTotal.ElapsedMilliseconds}ms");

            // Setup Logger that writes to xUnit output
            var logger = new XunitLogger<BbcHtmlParser>(_output);
            var jsonLogger = new XunitLogger<BbcJsonParser>(_output);
            var parser = new BbcJsonParser(jsonLogger);
            var htmlParser = new BbcHtmlParser(logger, parser);

            _output.WriteLine("Starting ParseHtml...");
            
            // Warmup (optional)
            // htmlParser.ParseHtml(html);

            var swParse = Stopwatch.StartNew();
            var fixtures = htmlParser.ParseHtml(html);
            swParse.Stop();

            _output.WriteLine($"--------------------------------------------------");
            _output.WriteLine($"Parse Result: {fixtures.Count} fixtures found.");
            _output.WriteLine($"Parse Time:   {swParse.ElapsedMilliseconds} ms");
            _output.WriteLine($"--------------------------------------------------");

            // Sanity assertions
            Assert.NotNull(fixtures);
            
            if (fixtures.Count > 0)
            {
                Assert.True(swParse.ElapsedMilliseconds < 100, $"Parser took too long: {swParse.ElapsedMilliseconds}ms");
            }
        }

        // Simple Logger for Test Output
        private class XunitLogger<T> : ILogger<T>
        {
            private readonly ITestOutputHelper _output;

            public XunitLogger(ITestOutputHelper output)
            {
                _output = output;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            }
        }
    }
}
