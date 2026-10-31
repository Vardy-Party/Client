using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class LocalLanPlayServiceCancellationTests
{
    [Fact]
    public async Task Play_SocketAbortFromCallerCancel_RethrowsAndKeepsDiscoveryCache()
    {
        var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/health", StringComparison.Ordinal) == true)
                return Json(HttpStatusCode.OK, """{"capabilities":["play.stream"]}""");

            playEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new HttpRequestException("socket aborted", new SocketException(125));
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var service = CreateService(handler);
        service.EndpointsForTests.Remember("http://10.0.0.4:5070", DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource();
        var pending = service.ResolveM3U8UrlAsync(
            "https://streams.example.test/match",
            playerStreamName: null,
            resolutionStrategy: null,
            source: null,
            cts.Token);
        await playEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(service.EndpointsForTests.HasFresh(DateTimeOffset.UtcNow));

        handler.Next = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"url":"https://cdn.example.test/live.m3u8"}"""));
        var resolved = await service.ResolveM3U8UrlAsync("https://streams.example.test/match");
        Assert.Equal("https://cdn.example.test/live.m3u8", resolved?.Url);
        Assert.True(service.EndpointsForTests.HasFresh(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Play_HttpFailure_ReturnsNull_KeepsEndpoint_AndCallsPlayOnce()
    {
        var handler = new ScriptHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler);
        service.DiscoverOverrideForTests = _ => Task.FromResult<string?>(null);
        service.EndpointsForTests.Remember("http://10.0.0.4:5070", DateTimeOffset.UtcNow);
        var plays = 0;
        handler.Next = (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref plays);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        };

        var resolved = await service.ResolveM3U8UrlAsync("https://streams.example.test/match");

        // The service answered, so this is a stream failure: null, one /play, and
        // the endpoint is not thrown away (no re-discovery, no second /play).
        Assert.Null(resolved);
        Assert.Equal(1, plays);
        Assert.True(service.EndpointsForTests.HasFresh(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Play_TimeoutWithoutCallerCancel_ThrowsInfrastructure_AndKeepsEndpoint()
    {
        var handler = new ScriptHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/play/", StringComparison.Ordinal) == true)
                throw new TaskCanceledException("linked timeout");
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var service = CreateService(handler);
        service.DiscoverOverrideForTests = _ => Task.FromResult<string?>(null);
        service.EndpointsForTests.Remember("http://10.0.0.4:5070", DateTimeOffset.UtcNow);

        // Our own timeout is an infrastructure failure, not a dead stream.
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(
            () => service.ResolveM3U8UrlAsync("https://streams.example.test/match"));

        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.True(service.EndpointsForTests.HasFresh(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Mp_SocketAbortFromCallerCancel_RethrowsAndKeepsDiscoveryCache()
    {
        var mpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"capabilities":["play.stream","mp.chrome"],"version":"9.9.9"}""");
            }

            if (path.EndsWith("/mp", StringComparison.Ordinal))
            {
                mpEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new HttpRequestException("socket aborted", new SocketException(125));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(handler);
        service.EndpointsForTests.Remember("http://10.0.0.4:5070", DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource();
        var pending = service.ResolveM3U8UrlAsync(
            "https://streams.example.test/match",
            playerStreamName: null,
            resolutionStrategy: "v2",
            source: "mp",
            cts.Token);
        await mpEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(service.EndpointsForTests.HasFresh(DateTimeOffset.UtcNow));
    }

    private static LocalLanPlayService CreateService(ScriptHandler handler)
    {
        var http = new HttpClient(handler);
        return new LocalLanPlayService(
            http,
            Options.Create(new GamesApiSettings
            {
                CallTimeoutSeconds = 5,
                MaxRetries = 0,
                RefreshSchedule = 60,
                M3U8CallTimeoutSeconds = 5
            }),
            Array.Empty<IPlaybackTransportPlugin>(),
            new SilentLogger());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class ScriptHandler : HttpMessageHandler
    {
        public ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next) =>
            Next = next;

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Next(request, cancellationToken);
    }

    private sealed class SilentLogger : ILogger<LocalLanPlayService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
