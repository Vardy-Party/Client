using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.Ports;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class LocalLanPlayServiceScrapeHealthPrivacyTests
{
    private const string Endpoint = "http://10.0.0.4:5070";
    private const string Match = "https://streams.example.test/match";

    [Fact]
    public async Task Resolve_WhenOptedOut_DoesNotSendScrapeHealthContextHeaders()
    {
        // Arrange
        HttpRequestMessage? playRequest = null;
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(false);
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
                playRequest = request;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        }, prefs, optedInContext: true);
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
        service.SetCapabilitiesForTests("play.stream", "scrape.health");

        // Act
        await service.ResolveM3U8UrlAsync(Match);

        // Assert
        Assert.NotNull(playRequest);
        Assert.False(playRequest!.Headers.Contains("X-VP-Api-Base-Url"));
        Assert.False(playRequest.Headers.Contains("X-VP-League"));
        Assert.False(playRequest.Headers.Contains("X-VP-Home"));
        Assert.False(playRequest.Headers.Contains("X-VP-Away"));
    }

    [Fact]
    public async Task Resolve_WhenOptedInWithCapability_SendsScrapeHealthContextHeaders()
    {
        // Arrange
        HttpRequestMessage? playRequest = null;
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(true);
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
                playRequest = request;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        }, prefs, optedInContext: true);
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
        service.SetCapabilitiesForTests("play.stream", "scrape.health");

        // Act
        await service.ResolveM3U8UrlAsync(Match);

        // Assert
        Assert.NotNull(playRequest);
        Assert.True(playRequest!.Headers.TryGetValues("X-VP-Api-Base-Url", out var apiBase));
        Assert.Equal("https://api.example.test", Assert.Single(apiBase));
        Assert.True(playRequest.Headers.TryGetValues("X-VP-League", out var league));
        Assert.Equal("League Alpha", Assert.Single(league));
        Assert.True(playRequest.Headers.TryGetValues("X-VP-Home", out var home));
        Assert.Equal("Home United", Assert.Single(home));
        Assert.True(playRequest.Headers.TryGetValues("X-VP-Away", out var away));
        Assert.Equal("Away City", Assert.Single(away));
        Assert.True(playRequest.Headers.TryGetValues("X-VP-Client-Os", out var clientOs));
        Assert.Equal("windows", Assert.Single(clientOs));
        Assert.True(playRequest.Headers.TryGetValues("X-VP-Client-Version", out var clientVersion));
        Assert.Equal("2.0.0-test", Assert.Single(clientVersion));
    }

    [Fact]
    public async Task Resolve_WhenOptedInWithoutScrapeHealthCapability_DoesNotSendContextHeaders()
    {
        // Arrange
        HttpRequestMessage? playRequest = null;
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(true);
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
                playRequest = request;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        }, prefs, optedInContext: true);
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
        service.SetCapabilitiesForTests("play.stream");

        // Act
        await service.ResolveM3U8UrlAsync(Match);

        // Assert
        Assert.NotNull(playRequest);
        Assert.False(playRequest!.Headers.Contains("X-VP-Api-Base-Url"));
        Assert.False(playRequest.Headers.Contains("X-VP-League"));
    }

    private static LocalLanPlayService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next,
        IStreamHealthDataCollectionPreferencesStore prefs,
        bool optedInContext)
    {
        var selection = new SelectionState();
        if (optedInContext)
        {
            selection.CurrentGame = new Game
            {
                Home = "Home United",
                Away = "Away City",
                ApiLeague = "League Alpha",
                Start = DateTime.UtcNow
            };
        }

        return new LocalLanPlayService(
            new HttpClient(new ScriptHandler(next)),
            Options.Create(new GamesApiSettings
            {
                CallTimeoutSeconds = 5,
                MaxRetries = 0,
                RefreshSchedule = 60,
                M3U8CallTimeoutSeconds = 5
            }),
            Array.Empty<IPlaybackTransportPlugin>(),
            NullLogger<LocalLanPlayService>.Instance,
            streamHealthDataCollection: prefs,
            apiSettings: Options.Create(new APISettings { HeadlessBaseUrl = "https://api.example.test/" }),
            selectionState: selection,
            clientEnvironment: new StubClientEnvironment());
    }

    private sealed class StubClientEnvironment : IStreamHealthClientEnvironment
    {
        public string? ClientOs => "windows";
        public string? ClientVersion => "2.0.0-test";
        public string? LocalServiceOs => null;
        public string? LocalServiceVersion => null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            next(request, cancellationToken);
    }
}
