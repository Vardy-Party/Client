using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.V2;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules: "CDN unreachable" is never "dead". A connect timeout,
/// refused or reset connection, or TLS failure on the manifest or a segment,
/// or a LocalService /play that could not reach the CDN, defers the row with
/// "CDN unreachable, rechecking". It is rechecked after 2 min, 5 min, then
/// every 10 min while the game is live, never reported and never dropped, and
/// joins the pool when it comes back Healthy. Real dead stays Failed: manifest
/// 404/410, "M3U8 not Found", decoy segments and NAME_NOT_RESOLVED.
/// Evidence: the MSI IP block from 19:02 to 19:44 UK time on 31 Oct 2026
/// marked 27 lshls rows plus durrfity, 643t8a, nothintd and instreams dead;
/// they were all Healthy after 19:52.
/// </summary>
public class CdnUnreachableBusinessRulesTests
{
    private const string Manifest = "https://hls.lshls.st/live/index.m3u8";
    private const string Segment = "https://hls.lshls.st/live/seg1.ts";
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    // ---------- health checker ----------

    private StreamHealthChecker Checker(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new ScriptHandler((r, _) => send(r))),
            NullLogger<StreamHealthChecker>.Instance,
            Options.Create(_fixture.Create<StreamHealthSettings>()));

    private static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "Connection refused (hls.lshls.st:443)", new SocketException(10061));

    private static HttpResponseMessage Playlist() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\n" + Segment)
    };

    public static TheoryData<string, StreamHealthStatus> ManifestTransportFailures => new()
    {
        { "refused", StreamHealthStatus.CdnUnreachable },
        { "timeout", StreamHealthStatus.CdnUnreachable },
        { "tls", StreamHealthStatus.CdnUnreachable },
        { "reset", StreamHealthStatus.CdnUnreachable },
        { "dns", StreamHealthStatus.HostNotResolved }
    };

    [Theory]
    [MemberData(nameof(ManifestTransportFailures))]
    public async Task HealthCheck_ManifestTransportFailure_IsCdnUnreachable_ExceptDns(string failure, StreamHealthStatus expected)
    {
        Exception ex = failure switch
        {
            "refused" => Refused(),
            "timeout" => new TaskCanceledException("The operation was canceled.", new TimeoutException("connect timed out")),
            "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established",
                new AuthenticationException("Authentication failed")),
            "reset" => new HttpRequestException(HttpRequestError.ResponseEnded, "Connection reset by peer",
                new System.IO.IOException("Connection reset by peer")),
            _ => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known (hls.gone.example:443)",
                new SocketException(11001))
        };
        var checker = Checker(_ => Task.FromException<HttpResponseMessage>(ex));

        var result = await checker.CheckStreamHealthAsync(Manifest, "https://player.example.test");

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, StreamHealthStatus.ManifestNotFound)]
    [InlineData(HttpStatusCode.Gone, StreamHealthStatus.ManifestNotFound)]
    [InlineData(HttpStatusCode.Forbidden, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(HttpStatusCode.BadGateway, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StreamHealthStatus.ManifestUnreachable)]
    public async Task HealthCheck_ManifestHttpStatus_OnlyNotFoundIsDead(HttpStatusCode code, StreamHealthStatus expected)
    {
        var checker = Checker(_ => Task.FromResult(new HttpResponseMessage(code)));

        var result = await checker.CheckStreamHealthAsync(Manifest, "https://player.example.test");

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected != StreamHealthStatus.ManifestNotFound, CdnReachabilityPolicy.IsCdnUnreachable(result.Status));
    }

    [Fact]
    public async Task HealthCheck_SegmentConnectFailure_IsCdnUnreachable()
    {
        var checker = Checker(r => r.RequestUri!.ToString() == Manifest
            ? Task.FromResult(Playlist())
            : Task.FromException<HttpResponseMessage>(Refused()));

        var result = await checker.CheckStreamHealthAsync(Manifest, "https://player.example.test");

        Assert.Equal(StreamHealthStatus.CdnUnreachable, result.Status);
    }

    [Fact]
    public async Task HealthCheck_DecoySegment_StaysDead()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };
        var checker = Checker(r =>
        {
            if (r.RequestUri!.ToString() == Manifest)
                return Task.FromResult(Playlist());
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        var result = await checker.CheckStreamHealthAsync(Manifest, "https://player.example.test");

        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
        Assert.False(CdnReachabilityPolicy.IsCdnUnreachable(result.Status));
    }

    // ---------- LocalService /play failure body ----------

    [Theory]
    [InlineData(504, "{\"error\":\"timeout\"}", true)]
    [InlineData(500, "{\"error\":\"page.goto: net::ERR_CONNECTION_TIMED_OUT at https://hls.lshls.st/embed\"}", true)]
    [InlineData(404, "{\"error\":\"M3U8 not Found\",\"diagnostics\":{\"failedRequestHosts\":[{\"host\":\"hls.lshls.st\",\"mediaLike\":true,\"errors\":[\"net::ERR_CONNECTION_REFUSED\"]}]}}", true)]
    [InlineData(404, "{\"Error\":\"M3U8 not Found\",\"Diagnostics\":{\"FailedRequestHosts\":[{\"Host\":\"hls.lshls.st\",\"MediaLike\":true,\"Pending\":3,\"Errors\":[]}]}}", true)]
    [InlineData(404, "{\"error\":\"M3U8 not Found\",\"diagnostics\":{\"failedRequestHosts\":[{\"host\":\"ads.example\",\"mediaLike\":false,\"errors\":[\"net::ERR_CONNECTION_REFUSED\"]}]}}", false)]
    [InlineData(404, "{\"error\":\"M3U8 not Found\",\"diagnostics\":{\"failedRequestHosts\":[{\"host\":\"cdn.gone\",\"mediaLike\":true,\"pending\":2,\"errors\":[\"net::ERR_NAME_NOT_RESOLVED\"]}]}}", false)]
    [InlineData(404, "{\"error\":\"M3U8 not Found\"}", false)]
    [InlineData(500, "{\"error\":\"net::ERR_NAME_NOT_RESOLVED\"}", false)]
    [InlineData(500, "upstream failed: net::ERR_CONNECTION_RESET", true)]
    [InlineData(500, "", false)]
    public void PlayFailureBody_CdnUnreachableOnlyOnConnectEvidence(int status, string body, bool expected)
        => Assert.Equal(expected, LocalServicePlayFailure.IsCdnUnreachable(status, body));

    [Fact]
    public async Task LocalService_PlayCouldNotReachCdn_ThrowsCdnUnreachable_NotRetryable()
    {
        var service = CreateLan(Json(HttpStatusCode.InternalServerError,
            "{\"error\":\"page.goto: net::ERR_CONNECTION_TIMED_OUT at https://hls.lshls.st/embed\"}"));

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(
            () => service.ResolveM3U8UrlAsync("https://streams.example.test/match"));

        Assert.Equal(LocalServiceUnavailableReason.CdnUnreachable, ex.Reason);
        Assert.False(ex.IsRetryable);
        Assert.Contains(CdnReachabilityPolicy.RecheckingMessage, ex.Message);
    }

    [Fact]
    public async Task LocalService_M3u8NotFound_IsStillNoPlaylist()
    {
        var service = CreateLan(Json(HttpStatusCode.NotFound, "{\"error\":\"M3U8 not Found\"}"));

        Assert.Null(await service.ResolveM3U8UrlAsync("https://streams.example.test/match"));
    }

    // ---------- resolver ----------

    private Mock<ILocalLanPlayService> ResolverMocks()
    {
        var lan = _fixture.GetMock<ILocalLanPlayService>();
        lan.SetupGet(s => s.UsesRemoteCompute).Returns(false);
        lan.SetupGet(s => s.LastRemoteComputeError).Returns((string?)null);
        lan.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _fixture.Inject<IDiscoveredChipNormalizer>(new V2PlaybackTransportPlugin());
        return lan;
    }

    private StreamModel CreateStream(string url = "https://streams.example.test/1", string channel = "lshls 1") =>
        _fixture.Build<StreamModel>().With(s => s.Url, url).With(s => s.Channel, channel).Create();

    private void HealthReturns(StreamHealthStatus status)
    {
        var health = new StreamHealth { Url = Manifest, Status = status };
        var checker = _fixture.GetMock<IStreamHealthChecker>();
        checker.Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);
        checker.Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StreamHealthProbe?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);
    }

    [Fact]
    public async Task Resolver_LocalServiceCouldNotReachCdn_IsDeferredForRecheck_NoInfrastructureRetry()
    {
        var lan = ResolverMocks();
        var stream = CreateStream();
        var calls = 0;
        lan.Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ThrowsAsync(new LocalServiceUnavailableException(LocalServiceUnavailableReason.CdnUnreachable,
                "CDN unreachable, rechecking (hls.lshls.st: net::ERR_CONNECTION_TIMED_OUT)"));
        var delays = new List<TimeSpan>();
        var resolver = _fixture.Create<StreamResolver>();
        resolver.DelayAsync = (d, _) => { delays.Add(d); return Task.CompletedTask; };

        var row = Assert.Single(await CollectAsync(resolver.ResolveStreamsIncrementallyAsync([stream])));

        Assert.Equal(StreamResolutionStatus.Deferred, row.Status);
        Assert.Equal(StreamDeferReason.CdnUnreachable, row.DeferReason);
        Assert.Contains(CdnReachabilityPolicy.RecheckingMessage, row.ErrorMessage);
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData(StreamHealthStatus.CdnUnreachable, StreamResolutionStatus.Deferred)]
    [InlineData(StreamHealthStatus.ManifestUnreachable, StreamResolutionStatus.Deferred)]
    [InlineData(StreamHealthStatus.ManifestNotFound, StreamResolutionStatus.Failed)]
    [InlineData(StreamHealthStatus.HostNotResolved, StreamResolutionStatus.Failed)]
    [InlineData(StreamHealthStatus.SegmentUnreachable, StreamResolutionStatus.Failed)]
    [InlineData(StreamHealthStatus.Healthy, StreamResolutionStatus.Healthy)]
    public async Task Resolver_HealthVerdict_CdnUnreachableDefers_DeadFails(StreamHealthStatus health, StreamResolutionStatus expected)
    {
        var lan = ResolverMocks();
        var stream = CreateStream();
        lan.Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = Manifest });
        HealthReturns(health);

        var row = Assert.Single(await CollectAsync(_fixture.Create<StreamResolver>().ResolveStreamsIncrementallyAsync([stream])));

        Assert.Equal(expected, row.Status);
        if (expected == StreamResolutionStatus.Deferred)
        {
            Assert.Equal(StreamDeferReason.CdnUnreachable, row.DeferReason);
            Assert.StartsWith(CdnReachabilityPolicy.RecheckingMessage, row.ErrorMessage);
            Assert.Equal(CdnReachabilityPolicy.RecheckingMessage, row.GetQualityDisplay());
        }
    }

    // ---------- orchestrator ----------

    private (StreamResolutionOrchestrator Sut, List<StreamHealthReport> Reports, List<TimeSpan> RecheckDelays, List<TimeSpan> LsDelays)
        Orchestrator(StreamModel stream, Func<int, EnrichedStream> resolveCall, Func<DateTime>? clock = null,
            Action<TimeSpan>? onRecheckDelay = null)
    {
        ResolverMocks();
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns([_fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()]);
        _fixture.Inject<IStreamSwitchingService>(_fixture.Create<StreamSwitchingService>());
        var resolves = 0;
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(It.IsAny<List<StreamModel>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns(() => Yield(resolveCall(++resolves)));
        var reports = new List<StreamHealthReport>();
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => { lock (reports) reports.Add(r); })
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IPlaybackLauncher>()
            .Setup(p => p.PlayVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var recheckDelays = new List<TimeSpan>();
        var lsDelays = new List<TimeSpan>();
        sut.DelayAsync = (d, _) => { lock (lsDelays) lsDelays.Add(d); return Task.CompletedTask; };
        sut.CdnRecheckDelayAsync = (d, _) =>
        {
            lock (recheckDelays) recheckDelays.Add(d);
            onRecheckDelay?.Invoke(d);
            return Task.CompletedTask;
        };
        if (clock != null)
            sut.UtcNow = clock;
        return (sut, reports, recheckDelays, lsDelays);
    }

    private static EnrichedStream CdnDeferred(StreamModel stream) => new()
    {
        Stream = stream,
        Status = StreamResolutionStatus.Deferred,
        DeferReason = StreamDeferReason.CdnUnreachable,
        ErrorMessage = $"{CdnReachabilityPolicy.RecheckingMessage} (CdnUnreachable)"
    };

    private Game LiveGame(DateTime kickOff) =>
        _fixture.Build<Game>().With(g => g.Start, kickOff).With(g => g.IsFinished, false).Create();

    [Fact]
    public async Task Orchestrator_CdnUnreachableRow_RecheckedAfterTwoMinutes_JoinsPoolWhenHealthy_NeverReportedFailed()
    {
        var kickOff = DateTime.UtcNow.AddMinutes(-50);
        var stream = CreateStream();
        var (sut, reports, recheckDelays, lsDelays) = Orchestrator(stream, call => call == 1
            ? CdnDeferred(stream)
            : new EnrichedStream
            {
                Stream = stream,
                Status = StreamResolutionStatus.Healthy,
                ResolvedM3U8Url = Manifest,
                Referer = stream.Url
            });

        var outcome = await sut.StartAsync(LiveGame(kickOff), _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Equal([TimeSpan.FromMinutes(2)], recheckDelays);
        Assert.Empty(lsDelays);
        Assert.Equal(0, outcome.CdnUnreachableStreamCount);
        Assert.False(outcome.LocalServiceUnavailable);
        StreamHealthReport[] snapshot;
        lock (reports) snapshot = reports.ToArray();
        Assert.Contains(snapshot, r => r.Status == "working" && r.StreamUrl == stream.Url);
        Assert.DoesNotContain(snapshot, r => r.Status is "failed" or "unknown");
    }

    [Fact]
    public async Task Orchestrator_CdnStaysUnreachable_RechecksTwoFiveThenTen_UntilGameNoLongerLive_NeverReportedOrDropped()
    {
        var kickOff = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);
        var now = kickOff.AddMinutes(2);
        var stream = CreateStream();
        var resolves = 0;
        var (sut, reports, recheckDelays, _) = Orchestrator(
            stream,
            call => { resolves = call; return CdnDeferred(stream); },
            clock: () => now,
            onRecheckDelay: d => now += d);

        var outcome = await sut.StartAsync(LiveGame(kickOff), _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Equal(TimeSpan.FromMinutes(2), recheckDelays[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), recheckDelays[1]);
        Assert.All(recheckDelays.Skip(2), d => Assert.Equal(TimeSpan.FromMinutes(10), d));
        Assert.True(now > kickOff + CdnReachabilityPolicy.MaxLiveWindow);
        // One recheck per wait that ended while the game was still live.
        Assert.Equal(recheckDelays.Count, resolves);
        Assert.Equal(1, outcome.CdnUnreachableStreamCount);
        Assert.False(outcome.LocalServiceUnavailable);
        Assert.True(outcome.NoWorkingStreams);
        lock (reports) Assert.Empty(reports);
    }

    [Fact]
    public async Task Orchestrator_FinishedGame_DoesNotRecheck_StillNotReported()
    {
        var stream = CreateStream();
        var (sut, reports, recheckDelays, _) = Orchestrator(stream, _ => CdnDeferred(stream));
        var game = _fixture.Build<Game>().With(g => g.IsFinished, true).Create();

        var outcome = await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Empty(recheckDelays);
        Assert.Equal(1, outcome.CdnUnreachableStreamCount);
        lock (reports) Assert.Empty(reports);
    }

    [Fact]
    public void PlaybackResult_CdnUnreachableClose_IsNoVerdict()
    {
        var result = PlaybackResult.Completed($"{PlaybackCloseReasons.CdnUnreachable}: ConnectException: Failed to connect");

        Assert.True(result.IsCdnUnreachable);
        Assert.True(result.IsLocalServiceUnavailable);
    }

    // ---------- helpers ----------

    private static LocalLanPlayService CreateLan(HttpResponseMessage playResponse)
    {
        var service = new LocalLanPlayService(
            new HttpClient(new ScriptHandler((request, _) =>
                request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true
                    ? Task.FromResult(playResponse)
                    : Task.FromResult(Json(HttpStatusCode.OK, "{}")))),
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
        service.EndpointsForTests.Remember("http://10.0.0.4:5070", DateTimeOffset.UtcNow);
        return service;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var s in streams)
        {
            await Task.Yield();
            yield return s;
        }
    }

    private static async Task<List<EnrichedStream>> CollectAsync(IAsyncEnumerable<EnrichedStream> source)
    {
        var list = new List<EnrichedStream>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            next(request, cancellationToken);
    }
}
