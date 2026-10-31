using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// The health check records <see cref="MirrorEvidence"/> from the media playlist it passed
/// (docs/STREAM_PLAYBACK_RULES.md "Proven mirrors"), and records none when the check fails.
/// </summary>
public class StreamHealthCheckerMirrorEvidenceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private const string Playlist =
        "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:157384\n" +
        "#EXTINF:6.000,\nsyaLqucEne4k-944307180.ts\n" +
        "#EXTINF:6.000,\nsyaLqucEne4k-944847180.ts\n";

    private StreamHealthChecker NewChecker(RoutingHandler handler) =>
        new(new HttpClient(handler), NullLogger<StreamHealthChecker>.Instance,
            Options.Create(_fixture.Create<StreamHealthSettings>()));

    [Fact]
    public async Task HealthyMediaPlaylist_RecordsStemClockAndTargetDuration()
    {
        var handler = new RoutingHandler();
        const string master = "http://edgestream2.example/hls/master.m3u8";
        const string media = "http://edgestream2.example/hls/syaLqucEne4k.m3u8";
        handler.AddResponse(master, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\nsyaLqucEne4k.m3u8")
        });
        handler.AddResponse(media, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Playlist)
        });
        handler.AddResponse("HEAD:http://edgestream2.example/hls/syaLqucEne4k-944307180.ts",
            () => new HttpResponseMessage(HttpStatusCode.OK));

        var health = await NewChecker(handler).CheckStreamHealthAsync(master, "http://player.example");

        Assert.Equal(StreamHealthStatus.Healthy, health.Status);
        var evidence = Assert.IsType<MirrorEvidence>(health.MirrorEvidence);
        Assert.Equal(master, evidence.SourceUrl);
        Assert.Equal(media, evidence.PlaylistUrl);
        Assert.Equal("syaLqucEne4k-{n}.ts", evidence.SegmentNameStem);
        Assert.True(evidence.SegmentNumbersArePts);
        Assert.Equal((long?)944847180, evidence.EdgeSegmentNumber);
        Assert.Equal((long?)157384, evidence.MediaSequence);
        Assert.Equal((double?)6, evidence.TargetDurationSeconds);
    }

    [Fact]
    public async Task FailedCheck_RecordsNoEvidence()
    {
        var handler = new RoutingHandler();
        const string media = "http://edgestream5.example/hls/syaLqucEne4k.m3u8";
        handler.AddResponse(media, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Playlist)
        });

        var health = await NewChecker(handler).CheckStreamHealthAsync(media, "http://player.example");

        Assert.NotEqual(StreamHealthStatus.Healthy, health.Status);
        Assert.Null(health.MirrorEvidence);
    }

    /// <summary>Fresh response per request (the checker reads the manifest twice).</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();

        public void AddResponse(string key, Func<HttpResponseMessage> response) => _routes[key] = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            var key = request.Method == HttpMethod.Head ? $"HEAD:{url}" : url;
            return Task.FromResult(_routes.TryGetValue(key, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
