using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules for <c>POST /mp</c> (31 Oct 2026): only "the service answered
/// and found no playlist" is a stream verdict (null). Unreachable, our timeout,
/// connection failure, an LS 5xx / NDJSON error 5xx, 401/403 and "V2/MP scrape
/// disabled" are infrastructure: <see cref="LocalServiceUnavailableException"/>,
/// so the resolver defers the row (never Failed, never reported).
/// </summary>
public class LocalLanPlayServiceMpInfrastructureTests
{
    private const string Endpoint = "http://10.0.0.4:5070";
    private const string Page = "https://streams.example.test/mp-match";
    private const string Health = """{"capabilities":["play.stream","mp.chrome"],"version":"9.9.9"}""";
    private const string Disabled = """{"error":"V2/MP scrape disabled on this local service"}""";

    [Fact]
    public async Task Mp503ScrapeDisabled_IsMpDisabled_NotRetryable_AndNotADeadStream()
    {
        var service = CreateService((_, _) => Task.FromResult(Json((HttpStatusCode)503, Disabled)), out _);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.MpDisabled, ex.Reason);
        Assert.False(ex.IsRetryable);
        Assert.IsAssignableFrom<InfrastructureUnavailableException>(ex);
    }

    [Fact]
    public async Task MpDisabled_IsRemembered_NoSecondPostWithinWindow_ThenAsksAgainAfterExpiry()
    {
        var now = new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);
        var service = CreateService((_, _) => Task.FromResult(Json((HttpStatusCode)503, Disabled)), out var counts);
        service.Clock = () => now;

        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));
        Assert.Equal(1, counts.Mp);

        // Every other MP row in the sweep: no POST, same "unavailable" answer.
        now += TimeSpan.FromSeconds(30);
        var again = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service, "https://streams.example.test/other"));
        Assert.Equal(LocalServiceUnavailableReason.MpDisabled, again.Reason);
        Assert.Equal(1, counts.Mp);

        // The operator may turn MP back on: ask again once the memory lapses.
        now += LocalLanPlayService.MpDisabledMemory;
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));
        Assert.Equal(2, counts.Mp);
    }

    [Fact]
    public async Task Mp500_IsServiceError_Retryable()
    {
        var service = CreateService(
            (_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, """{"error":"v2 scrape plugin not registered"}""")),
            out _);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task MpNdjsonErrorEvent500_IsServiceError()
    {
        var service = CreateService(
            (_, _) => Task.FromResult(Ndjson(HttpStatusCode.OK,
                """{"type":"chips","streams":["Chip A"]}""",
                """{"type":"error","statusCode":500,"error":"mp play execution failed"}""")),
            out _);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);
    }

    [Fact]
    public async Task MpNdjson404NotCaptured_IsAGenuineMiss_Null()
    {
        var service = CreateService(
            (_, _) => Task.FromResult(Ndjson(HttpStatusCode.OK,
                """{"type":"error","statusCode":404,"error":"M3U8 not captured from MP page."}""")),
            out _);

        var result = await ResolveMp(service);

        Assert.True(result is null || string.IsNullOrWhiteSpace(result.Url));
    }

    [Fact]
    public async Task Mp404_IsAGenuineMiss_Null()
    {
        var service = CreateService(
            (_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, """{"error":"M3U8 not captured from MP page."}""")),
            out _);

        var result = await ResolveMp(service);

        Assert.True(result is null || string.IsNullOrWhiteSpace(result.Url));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task MpAuthRejected_IsUnauthorized_NotRetryable(HttpStatusCode status)
    {
        var service = CreateService((_, _) => Task.FromResult(Json(status, "{}")), out _);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.Unauthorized, ex.Reason);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task MpConnectionRefused_IsConnectionFailed()
    {
        var service = CreateService(
            (_, _) => throw new HttpRequestException("refused", new SocketException(10061)),
            out _,
            healthAnswers: false);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.ConnectionFailed, ex.Reason);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task MpOurTimeout_ServiceAlive_IsCallTimeout()
    {
        var service = CreateService(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, "{}");
            },
            out _);
        service.MpHarvestTimeout = TimeSpan.FromMilliseconds(100);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => ResolveMp(service));

        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
    }

    [Fact]
    public async Task MpSuccess_StillResolves()
    {
        var service = CreateService(
            (_, _) => Task.FromResult(Ndjson(HttpStatusCode.OK,
                """{"type":"chips","streams":["Chip A"],"selectedStream":"Chip A"}""",
                """{"type":"chip","label":"Chip A","url":"https://cdn.example.test/mp.m3u8","rewrittenSegments":["https://cdn.example.test/a.exe?_s2=1"]}""",
                """{"type":"done"}""")),
            out _);

        var result = await ResolveMp(service);

        Assert.Equal("https://cdn.example.test/mp.m3u8", result?.Url);
    }

    [Theory]
    [InlineData("""{"error":"V2/MP scrape disabled on this local service"}""", true)]
    [InlineData("""{"error":"v2 scrape plugin not registered"}""", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMpScrapeDisabled_MatchesOnlyTheGateMessage(string? body, bool expected)
        => Assert.Equal(expected, LocalLanPlayService.IsMpScrapeDisabled(body));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Play401Or403_IsUnauthorized_NotANullVerdict(HttpStatusCode status)
    {
        var service = CreateService((_, _) => Task.FromResult(Json(status, "{}")), out _);

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(
            () => service.ResolveM3U8UrlAsync("https://streams.example.test/play-match"));

        Assert.Equal(LocalServiceUnavailableReason.Unauthorized, ex.Reason);
    }

    private static Task<M3U8Response?> ResolveMp(LocalLanPlayService service, string page = Page)
        => service.ResolveM3U8UrlAsync(page, playerStreamName: null, resolutionStrategy: "v2", source: "mp", CancellationToken.None);

    private sealed class Counts
    {
        public int Mp;
    }

    private static LocalLanPlayService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> work,
        out Counts counts,
        bool healthAnswers = true)
    {
        var c = new Counts();
        counts = c;
        var service = new LocalLanPlayService(
            new HttpClient(new ScriptHandler((request, ct) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/health", StringComparison.Ordinal))
                {
                    if (!healthAnswers)
                        throw new HttpRequestException("refused", new SocketException(10061));
                    return Task.FromResult(Json(HttpStatusCode.OK, Health));
                }

                if (path.EndsWith("/mp", StringComparison.Ordinal))
                    Interlocked.Increment(ref c.Mp);
                return work(request, ct);
            })),
            Options.Create(new GamesApiSettings
            {
                CallTimeoutSeconds = 5,
                MaxRetries = 0,
                RefreshSchedule = 60,
                M3U8CallTimeoutSeconds = 5
            }),
            Array.Empty<IPlaybackTransportPlugin>(),
            NullLogger<LocalLanPlayService>.Instance);
        service.DiscoverOverrideForTests = _ => Task.FromResult<string?>(null);
        service.DelayAsync = (_, _) => Task.CompletedTask;
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
        return service;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ndjson(HttpStatusCode status, params string[] lines) =>
        new(status) { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, MpNdjson.ContentType) };

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            next(request, cancellationToken);
    }
}