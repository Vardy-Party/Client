using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;

namespace VardyParty.Streaming.Tests;

public class StreamHealthCheckerBitstreamSizeTests
{
    [Fact]
    public async Task HealthyHeadPath_DoesNotBlockOnBitstreamProbe()
    {
        var ts = VideoBitstreamFixtures.MpegTs(
            VideoBitstreamFixtures.AnnexB(VideoBitstreamFixtures.AvcSps(1920, 1080)));
        var handler = new ScriptedHandler();
        var manifest = "http://streams.example.com/live.m3u8";
        var segment = "http://streams.example.com/seg.ts";
        handler.Playlist(manifest, "#EXTM3U\n#EXTINF:4,\nseg.ts\n");
        handler.Head(segment, "video/mp2t");
        handler.GetBytes(segment, ts, delay: TimeSpan.FromMilliseconds(1200));

        var checker = Create(handler);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var health = await checker.CheckStreamHealthAsync(manifest, "http://player.example.com");
        sw.Stop();

        Assert.Equal(StreamHealthStatus.Healthy, health.Status);
        Assert.Null(health.ProbedVideoHeight);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(700),
            $"Health check blocked on bitstream probe: {sw.Elapsed.TotalMilliseconds:0}ms");

        await health.WhenBitstreamSizeProbed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1920, health.ProbedVideoWidth);
        Assert.Equal(1080, health.ProbedVideoHeight);
    }

    [Fact]
    public async Task ClassifyBytesPath_ParsesHeightFromAlreadyFetchedPrefix()
    {
        var ts = VideoBitstreamFixtures.MpegTs(
            VideoBitstreamFixtures.AnnexB(VideoBitstreamFixtures.AvcSps(1280, 720)));
        var handler = new ScriptedHandler();
        var manifest = "http://streams.example.com/live.m3u8";
        var segment = "http://streams.example.com/seg.image";
        handler.Playlist(manifest, "#EXTM3U\n#EXTINF:4,\nseg.image\n");
        handler.GetBytes(segment, ts);

        var checker = Create(handler);
        var health = await checker.CheckStreamHealthAsync(manifest, "http://player.example.com");

        Assert.Equal(StreamHealthStatus.Healthy, health.Status);
        Assert.Equal(1280, health.ProbedVideoWidth);
        Assert.Equal(720, health.ProbedVideoHeight);
        Assert.True(health.WhenBitstreamSizeProbed.IsCompleted);
    }

    [Fact]
    public async Task EncryptedPlaylist_DoesNotFetchSegmentBodyForHeight()
    {
        var handler = new ScriptedHandler();
        var manifest = "http://streams.example.com/live.m3u8";
        var segment = "http://streams.example.com/seg.ts";
        handler.Playlist(manifest, """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="https://streams.example.com/key"
            #EXTINF:4,
            seg.ts
            """);
        handler.Head(segment, "video/mp2t");

        var checker = Create(handler);
        var health = await checker.CheckStreamHealthAsync(manifest, "http://player.example.com");
        await health.WhenBitstreamSizeProbed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StreamHealthStatus.Healthy, health.Status);
        Assert.Null(health.ProbedVideoHeight);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri!.ToString() == segment);
    }

    [Fact]
    public async Task InitMap_IsPreferredOverMediaSegment()
    {
        var init = VideoBitstreamFixtures.AvcCBox(VideoBitstreamFixtures.AvcSps(1920, 1080));
        var handler = new ScriptedHandler();
        var manifest = "http://streams.example.com/live.m3u8";
        var map = "http://streams.example.com/init.mp4";
        var segment = "http://streams.example.com/seg.m4s";
        handler.Playlist(manifest, """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4,
            seg.m4s
            """);
        handler.Head(segment, "video/mp4");
        handler.GetBytes(map, init);

        var checker = Create(handler);
        var health = await checker.CheckStreamHealthAsync(manifest, "http://player.example.com");
        await health.WhenBitstreamSizeProbed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StreamHealthStatus.Healthy, health.Status);
        Assert.Equal(1920, health.ProbedVideoWidth);
        Assert.Equal(1080, health.ProbedVideoHeight);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri!.ToString() == map);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri!.ToString() == segment);
    }

    private static StreamHealthChecker Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<StreamHealthChecker>.Instance,
            Options.Create(new StreamHealthSettings
            {
                ManifestTimeoutSeconds = 5,
                SegmentTimeoutSeconds = 5,
                MaxParallelStreams = 2
            }));

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];

        public void Playlist(string url, string body) =>
            _routes[url] = _ => Text(body, "application/vnd.apple.mpegurl");

        public void Head(string url, string contentType) =>
            _routes[$"HEAD:{url}"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x47])
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
                }
            };

        public void GetBytes(string url, byte[] body, TimeSpan? delay = null) =>
            _routes[url] = _ =>
            {
                if (delay is { } wait)
                    Thread.Sleep(wait);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp2t") }
                    }
                };
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(0, body.Length - 1, body.Length);
                return response;
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri?.ToString() ?? "";
            if (request.Method == HttpMethod.Head && _routes.TryGetValue($"HEAD:{url}", out var head))
                return Task.FromResult(head(request));
            if (_routes.TryGetValue(url, out var get))
                return Task.FromResult(get(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string body, string type) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type) }
                }
            };
    }
}
