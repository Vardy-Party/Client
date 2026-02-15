using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests;

public class RefererHandlingTests
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task ResolveStreams_PassesPlayRefererToHealthChecker()
    {
        // Arrange
        var stream = new Stream { Url = "http://source.example/stream1", Channel = "C1" };
        var settings = _fixture.Build<APISettings>()
            .With(a => a.HeadlessBaseUrl, "https://api.test")
            .Create();

        var handler = new FakeHttpHandler();
        var playUrl = $"https://api.test/play/{Uri.EscapeDataString(stream.Url)}";
        var m3u8Resp = new M3U8Response { Url = "https://cdn.test/playlist.m3u8" };
        handler.AddResponse(playUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(m3u8Resp), Encoding.UTF8, "application/json")
        });

        var http = new HttpClient(handler);

        var healthChecker = new CapturingHealthChecker();
        var gamesApiSettings = _fixture.Create<GamesApiSettings>();

        var resolver = new StreamResolver(http, healthChecker, Options.Create(settings),
            Options.Create(gamesApiSettings), NullLogger<StreamResolver>.Instance);

        // Act
        var list = new List<EnrichedStream>();
        await foreach (var es in resolver.ResolveStreamsIncrementallyAsync(new List<Stream> { stream })) list.Add(es);

        // Assert
        Assert.Single(list);
        Assert.Equal(stream.Url, healthChecker.CapturedReferer);
        Assert.Equal("https://cdn.test/playlist.m3u8", healthChecker.CapturedM3U8);
    }

    [Fact]
    public async Task CheckStreamHealth_UsesRefererForManifestAndSegmentRequests()
    {
        // Arrange
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);

        var m3u8Url = "https://cdn.test/playlist.m3u8";
        var segmentUrl = "https://cdn.test/segment.ts";

        var manifest = "#EXTM3U\n#EXTINF:10,\n" + segmentUrl;

        handler.AddResponse(m3u8Url, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(manifest, Encoding.UTF8, "application/vnd.apple.mpegurl")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK));

        var settings = _fixture.Build<APISettings>().Create();
        var gamesApiSettings = _fixture.Create<GamesApiSettings>();
        var streamHealthSettings = _fixture.Create<StreamHealthSettings>();

        var checker = new StreamHealthChecker(http, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(streamHealthSettings));

        var referer = "https://page.example/play/stream1";

        // Act
        var health = await checker.CheckStreamHealthAsync(m3u8Url, referer);

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, health.Status);

        var manifestRequests = handler.Requests
            .Where(r => string.Equals(r.RequestUri?.ToString(), m3u8Url, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(manifestRequests);
        foreach (var req in manifestRequests)
            Assert.True(req.Headers.Referrer != null && req.Headers.Referrer.ToString() == referer,
                "Manifest request missing referer header");

        var headReq =
            handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Head && r.RequestUri?.ToString() == segmentUrl);
        Assert.NotNull(headReq);
        Assert.True(headReq.Headers.Referrer != null && headReq.Headers.Referrer.ToString() == referer,
            "Segment HEAD missing referer header");
    }

    private class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = new();

        public void AddResponse(string url, HttpResponseMessage response)
        {
            _responses[url] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = request.RequestUri?.ToString() ?? string.Empty;

            if (request.Method == HttpMethod.Head)
            {
                var headKey = $"HEAD:{key}";
                if (_responses.TryGetValue(headKey, out var hr)) return Task.FromResult(hr);
            }

            if (_responses.TryGetValue(key, out var resp)) return Task.FromResult(resp);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private class CapturingHealthChecker : IStreamHealthChecker
    {
        public string? CapturedM3U8;
        public string? CapturedReferer;

        public Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl,
            CancellationToken cancellationToken = default)
        {
            CapturedReferer = refererUrl;
            CapturedM3U8 = m3u8Url;
            var h = new StreamHealth { Url = m3u8Url, Status = StreamHealthStatus.Healthy };
            return Task.FromResult(h);
        }
    }
}