using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamHealthCheckerTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task CheckStreamHealth_Healthy_DirectSegment()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n#EXTINF:10,\n" + segmentUrl)
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
        Assert.Equal(manifestUrl, result.Url);
    }

    [Fact]
    public async Task CheckStreamHealth_ManifestUnreachable_404()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/lost.m3u8";

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.ManifestUnreachable, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_InvalidManifest_NoTags()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/bad.m3u8";
        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Just some text\nNo M3U8 headers")
        });

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.InvalidManifest, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_EmptyManifest_JustHeader()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/empty.m3u8";
        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n")
        });

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.EmptyManifest, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_MasterPlaylist_RecursiveCheck_Success()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var masterUrl = "http://streams.example.com/master.m3u8";
        var childUrl = "http://streams.example.com/child.m3u8";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(masterUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1280000\n{childUrl}")
        });

        handler.AddResponse(childUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:10,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(masterUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_MasterPlaylist_RecursionLimit()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        void AddChain(int i)
        {
            var url = $"http://streams.example.com/list{i}.m3u8";
            var next = $"http://streams.example.com/list{i + 1}.m3u8";
            handler.AddResponse(url, new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\n{next}")
            });
        }

        AddChain(0);
        AddChain(1);
        AddChain(2);
        AddChain(3);

        // Act
        var result = await checker.CheckStreamHealthAsync("http://streams.example.com/list0.m3u8", "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.InvalidManifest, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_SegmentUnreachable()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:10,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_SegmentHeadForbidden_GetRangeSuccess()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:10,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.Forbidden));
        handler.AddResponse(segmentUrl, new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_SegmentHeadFail_GetSuccess()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:10,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        handler.AddResponse(segmentUrl, new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_ImageSegment_Rejected()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl =
            "https://p16-common-sign.tiktokcdn-us.com/tos-useast8-v-5300-tx2/abc~tplv-tiktokx-origin.image?dr=1";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:4.0,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_SkipsImageSegment_ThenHealthyTs()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var imageUrl = "https://cdn.example.com/poster.image";
        var segmentUrl = "http://streams.example.com/segment.ts";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:1,\n{imageUrl}\n#EXTINF:4,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckStreamHealth_SegmentHeadOk_ImageContentType_Rejected()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler);
        var settingsProvider = _fixture.Create<StreamHealthSettings>();
        var checker = new StreamHealthChecker(client, NullLogger<StreamHealthChecker>.Instance,
            Options.Create(settingsProvider));

        var manifestUrl = "http://streams.example.com/playlist.m3u8";
        var segmentUrl = "http://streams.example.com/seg.bin";

        handler.AddResponse(manifestUrl, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:10,\n{segmentUrl}")
        });

        handler.AddResponse($"HEAD:{segmentUrl}", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") } }
        });

        // Act
        var result = await checker.CheckStreamHealthAsync(manifestUrl, "http://player.example.com");

        // Assert
        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new();

        public void AddResponse(string url, HttpResponseMessage response)
        {
            _responses[url] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";

            if (request.Method == HttpMethod.Head && _responses.TryGetValue($"HEAD:{url}", out var headResp))
                return Task.FromResult(headResp);
            if (request.Method == HttpMethod.Get && _responses.TryGetValue(url, out var getResp))
                return Task.FromResult(getResp);
            if (_responses.TryGetValue(url, out var anyResp)) return Task.FromResult(anyResp);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
