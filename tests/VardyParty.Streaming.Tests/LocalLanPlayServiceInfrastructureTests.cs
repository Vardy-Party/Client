using System;
using System.Collections.Generic;
using System.Linq;
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
/// Business rules: a local-service problem is never a dead stream. These
/// cover the 31 Oct 2026 TV logs: a double /play at 16:49:54 and 16:50:14 UK
/// time, and re-discovery from 16:51:43 to 16:53:03 UK time.
/// </summary>
public class LocalLanPlayServiceInfrastructureTests
{
    private const string Endpoint = "http://10.0.0.4:5070";
    private const string Match = "https://streams.example.test/match";

    [Fact]
    public async Task NoEndpoint_NotPaired_ThrowsEndpointUnknown_AfterBoundedDiscoveryRetries()
    {
        var delays = new List<TimeSpan>();
        var discoveries = 0;
        var service = CreateService((_, _) => throw new InvalidOperationException("no HTTP without an endpoint"));
        service.DiscoverOverrideForTests = _ =>
        {
            Interlocked.Increment(ref discoveries);
            return Task.FromResult<string?>(null);
        };
        service.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => service.ResolveM3U8UrlAsync(Match));

        Assert.Equal(LocalServiceUnavailableReason.EndpointUnknown, ex.Reason);
        Assert.Equal(LocalLanPlayService.DiscoveryRetryDelays, delays);
        Assert.True(discoveries >= 1);
    }

    [Fact]
    public async Task ConnectionFailure_Throws_AndInvalidatesOnlyAfterThreeInARow()
    {
        var service = CreateService((_, _) => throw new HttpRequestException("refused", new SocketException(10061)));
        service.DiscoverOverrideForTests = _ => Task.FromResult<string?>(null);
        service.DelayAsync = (_, _) => Task.CompletedTask;
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);

        var first = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => service.ResolveM3U8UrlAsync(Match));

        Assert.Equal(LocalServiceUnavailableReason.ConnectionFailed, first.Reason);
        Assert.Equal(Endpoint, service.EndpointsForTests.LastGood);

        for (var i = 0; i < LocalLanPlayService.ConnectionFailuresBeforeInvalidate && service.EndpointsForTests.LastGood != null; i++)
            await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => service.ResolveM3U8UrlAsync(Match));

        Assert.Null(service.EndpointsForTests.LastGood);
    }

    [Fact]
    public async Task ServiceAnswer_ResetsConnectionFailureCount()
    {
        var fail = true;
        var service = CreateService((request, _) =>
        {
            if (fail)
                throw new HttpRequestException("refused", new SocketException(10061));
            return Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        });
        service.DiscoverOverrideForTests = _ => Task.FromResult<string?>(null);
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => service.ResolveM3U8UrlAsync(Match));
        Assert.True(service.ConsecutiveConnectionFailuresForTests > 0);

        fail = false;
        var resolved = await service.ResolveM3U8UrlAsync(Match);

        Assert.Equal("https://cdn.example.test/live.m3u8", resolved?.Url);
        Assert.Equal(0, service.ConsecutiveConnectionFailuresForTests);
    }

    [Fact]
    public async Task ConcurrentResolvesOfOneStream_ShareOnePlayCall()
    {
        var plays = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) != true)
                return Json(HttpStatusCode.OK, "{}");
            Interlocked.Increment(ref plays);
            entered.TrySetResult();
            await release.Task;
            return Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}""");
        });
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);

        var first = service.ResolveM3U8UrlAsync(Match);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ResolveM3U8UrlAsync(Match);
        release.TrySetResult();

        Assert.Equal("https://cdn.example.test/live.m3u8", (await first)?.Url);
        Assert.Equal("https://cdn.example.test/live.m3u8", (await second)?.Url);
        Assert.Equal(1, plays);
    }

    [Fact]
    public async Task OneCallerCancelling_DoesNotCancelTheOtherWaiter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(async (request, ct) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) != true)
                return Json(HttpStatusCode.OK, "{}");
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}""");
        });
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource();

        var leaving = service.ResolveM3U8UrlAsync(Match, null, null, null, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var staying = service.ResolveM3U8UrlAsync(Match);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        release.TrySetResult();

        Assert.Equal("https://cdn.example.test/live.m3u8", (await staying)?.Url);
    }

    [Fact]
    public async Task DifferentStreams_AreNotMerged()
    {
        var plays = 0;
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
                Interlocked.Increment(ref plays);
            return Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        });
        service.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);

        await Task.WhenAll(
            service.ResolveM3U8UrlAsync(Match),
            service.ResolveM3U8UrlAsync(Match, "Channel East", "v2", null));

        Assert.Equal(2, plays);
    }

    private static LocalLanPlayService CreateService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next)
        => new(
            new HttpClient(new ScriptHandler(next)),
            Options.Create(new GamesApiSettings
            {
                CallTimeoutSeconds = 5,
                MaxRetries = 0,
                RefreshSchedule = 60,
                M3U8CallTimeoutSeconds = 5
            }),
            Array.Empty<IPlaybackTransportPlugin>(),
            NullLogger<LocalLanPlayService>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            next(request, cancellationToken);
    }
}