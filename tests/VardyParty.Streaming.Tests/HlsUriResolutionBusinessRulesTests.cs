using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rule (31 Oct 2026, Milan v Inter, v223 TV): a stream whose playlist lists
/// root-relative segments (<c>/hls-segments/&lt;hash&gt;/6195.ts?e=...</c>, hls.lshls.st) is
/// healthy when those segments are served. It must never be dropped or reported
/// SegmentUnreachable because the client failed to resolve the URI. Health resolves
/// every playlist URI against the playlist URL on every platform.
/// </summary>
public class HlsUriResolutionBusinessRulesTests
{
    private const string Master = "https://hls.example.invalid/live/abc/master.m3u8?t=1";
    private const string Variant = "https://hls.example.invalid/live/abc/720/index.m3u8?t=1";
    private const string Segment = "https://hls.example.invalid/hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9";

    [Fact]
    public async Task RootRelativeSegments_AreHealthy_NotSegmentUnreachable()
    {
        var cdn = new Cdn();
        cdn.Add(Variant, "application/vnd.apple.mpegurl",
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\n/hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9\n");
        cdn.Add(Segment, "video/mp2t", WrappedPngBuilder.FakeTs(20));

        var result = await Checker(cdn).CheckStreamHealthAsync(Variant, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
        Assert.NotEqual(StreamHealthStatus.SegmentUnreachable, result.Status);
        Assert.Contains(cdn.Requests, r => r == Segment);
        Assert.All(cdn.Requests, r => Assert.True(HlsUriResolver.IsHttpUrl(r), r));
    }

    [Fact]
    public async Task RootRelativeVariant_ThenRootRelativeSegments_AreHealthy()
    {
        var cdn = new Cdn();
        cdn.Add(Master, "application/vnd.apple.mpegurl",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\n/live/abc/720/index.m3u8?t=1\n");
        cdn.Add(Variant, "application/vnd.apple.mpegurl",
            "#EXTM3U\n#EXTINF:6.0,\n/hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9\n");
        cdn.Add(Segment, "video/mp2t", WrappedPngBuilder.FakeTs(20));

        var result = await Checker(cdn).CheckStreamHealthAsync(Master, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
        Assert.Contains(cdn.Requests, r => r == Variant);
        Assert.Contains(cdn.Requests, r => r == Segment);
    }

    [Fact]
    public async Task RootRelativeSegments_ThatAreReallyGone_StillFailForAStreamReason()
    {
        var cdn = new Cdn();
        cdn.Add(Variant, "application/vnd.apple.mpegurl",
            "#EXTM3U\n#EXTINF:6.0,\n/hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9\n");

        var result = await Checker(cdn).CheckStreamHealthAsync(Variant, "https://page.example.invalid/");

        // The CDN was actually asked (404): a real stream failure, not a client URI bug.
        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
        Assert.Contains(cdn.Requests, r => r == Segment);
    }

    private static StreamHealthChecker Checker(Cdn cdn) =>
        new(new HttpClient(cdn), NullLogger<StreamHealthChecker>.Instance,
            Options.Create(new StreamHealthSettings { ManifestTimeoutSeconds = 5, SegmentTimeoutSeconds = 5, MaxParallelStreams = 2 }));

    /// <summary>Fake CDN keyed by absolute URL; honours Range.</summary>
    private sealed class Cdn : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Type, byte[] Body)> _objects = new();
        public List<string> Requests { get; } = [];

        public void Add(string url, string type, string body) => Add(url, type, Encoding.UTF8.GetBytes(body));
        public void Add(string url, string type, byte[] body) => _objects[url] = (type, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
                Requests.Add(url);

            if (!_objects.TryGetValue(url, out var obj))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var body = obj.Body;
            var status = HttpStatusCode.OK;
            var r = request.Headers.Range?.Ranges.FirstOrDefault();
            if (r?.From is long from && from < body.Length)
            {
                var to = Math.Min(r.To ?? body.Length - 1, body.Length - 1);
                body = body[(int)from..((int)to + 1)];
                status = HttpStatusCode.PartialContent;
            }

            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body);
            content.Headers.ContentType = new MediaTypeHeaderValue(obj.Type);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
