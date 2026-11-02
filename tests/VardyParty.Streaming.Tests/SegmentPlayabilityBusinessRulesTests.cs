using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Segment playability business rules (docs/STREAM_PLAYBACK_RULES.md, "Segment
/// payloads"): health and every client player decide on segment BYTES, not on
/// URL or Content-Type. PNG-wrapped MPEG-TS (TikTok CDN <c>TIKTIKPX</c>) is
/// playable on every platform; plain image placeholders never are; soak headers
/// are used for probing but never replace classification.
/// </summary>
public class SegmentPlayabilityBusinessRulesTests
{
    private const string Manifest = "https://edge.example.invalid/premium1/index.m3u8?t=1";
    private const string TikTokSegment =
        "https://p16-common-sign.tiktokcdn-us.com/tos-useast8-v-0000/abc~tplv-tiktokx-origin.image?dr=1";

    /// <summary>How each client platform's player gets segment bytes.</summary>
    public enum PlayerPath
    {
        /// <summary>Windows AdaptiveMediaSource DownloadRequested and Linux LibVLC loopback proxy: whole body.</summary>
        WholeBody,
        /// <summary>Android Media3 UnwrappingDataSource: peek then pull through Read.</summary>
        PullStream,
        /// <summary>Async stream (Linux proxy style when streaming).</summary>
        AsyncStream
    }

    [Theory]
    [InlineData(PlayerPath.WholeBody)]
    [InlineData(PlayerPath.PullStream)]
    [InlineData(PlayerPath.AsyncStream)]
    public async Task PngWrappedTsSegments_ArePlayableOnEveryPlatform(PlayerPath path)
    {
        var ts = RealTsOrFake();
        var png = WrappedPngBuilder.Wrap(ts);

        var played = await PlayAsync(path, png);

        Assert.True(played.Unwrapped);
        Assert.Equal(ts, played.Bytes);
        Assert.Equal(SegmentPayloadKind.MpegTs, SegmentPayload.Classify(played.Bytes));
    }

    [Fact]
    public async Task PngWrappedTsStream_IsHealthy_EvenWithImageUrlAndImageContentType()
    {
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment));
        cdn.Add(TikTokSegment, "image/png", WrappedPngBuilder.Wrap(RealTsOrFake()));

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(PlayerPath.WholeBody)]
    [InlineData(PlayerPath.PullStream)]
    [InlineData(PlayerPath.AsyncStream)]
    public async Task WebpExifWrappedTsSegments_ArePlayableOnEveryPlatform(PlayerPath path)
    {
        var ts = RealTsOrFake();
        var webp = WrappedPngBuilder.Webp(ts);

        var played = await PlayAsync(path, webp);

        Assert.True(played.Unwrapped);
        Assert.Equal(ts, played.Bytes);
        Assert.Equal(SegmentPayloadKind.MpegTs, SegmentPayload.Classify(played.Bytes));
    }

    [Fact]
    public async Task RealCapturedWebpSegmentHead_IsHealthy()
    {
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment));
        cdn.Add(TikTokSegment, "image/webp", WrappedPngBuilder.Fixture("tiktok-webp-real-head-64k.webp.bin"));

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(PlayerPath.WholeBody)]
    [InlineData(PlayerPath.PullStream)]
    [InlineData(PlayerPath.AsyncStream)]
    public async Task PlainWebpPlaceholders_AreNeverConvertedToVideo(PlayerPath path)
    {
        var played = await PlayAsync(path, WrappedPngBuilder.Webp(null));

        Assert.False(played.Unwrapped);
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(played.Bytes));
    }

    [Fact]
    public async Task RealCapturedSegmentHead_IsHealthy()
    {
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment));
        cdn.Add(TikTokSegment, "image/png", WrappedPngBuilder.Fixture("tiktikpx-real-head-64k.png.bin"));

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
        Assert.Contains(cdn.Requests, r => r.Url == TikTokSegment && r.Range == "bytes=0-65535");
    }

    [Theory]
    [InlineData(PlayerPath.WholeBody)]
    [InlineData(PlayerPath.PullStream)]
    [InlineData(PlayerPath.AsyncStream)]
    public async Task PlainImagePlaceholders_AreNeverConvertedToVideo(PlayerPath path)
    {
        var placeholder = PlainPng();

        var played = await PlayAsync(path, placeholder);

        Assert.False(played.Unwrapped);
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(played.Bytes));
    }

    [Theory]
    [MemberData(nameof(Placeholders))]
    public async Task StreamOfPlainImagePlaceholders_IsRejected(string contentType, byte[] body)
    {
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment, TikTokSegment + "&n=2"));
        cdn.Add(TikTokSegment, contentType, body);
        cdn.Add(TikTokSegment + "&n=2", contentType, body);

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
    }

    public static IEnumerable<object[]> Placeholders()
    {
        yield return ["image/png", PlainPng()];
        yield return ["image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 }];
        // Marker present but not TS inside: still a placeholder.
        yield return ["image/png", WrappedPngBuilder.Png(WrappedPngBuilder.WrapperPixels(new byte[4000]), 512, 2)];
        yield return ["text/html", "<html>403</html>"u8.ToArray()];
        yield return ["image/webp", WrappedPngBuilder.Webp(null)];
    }

    [Fact]
    public async Task ImageBytesBehindVideoLookingUrl_AreRejected()
    {
        const string segment = "https://cdn.example.invalid/live/seg001.ts";
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(segment));
        cdn.Add(segment, "image/png", PlainPng());

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
    }

    [Fact]
    public async Task NeverTrustSoak_WithoutClassification()
    {
        var response = new M3U8Response
        {
            Url = Manifest,
            RequestHeaders = new Dictionary<string, string>
            {
                ["cookie"] = "cf=1",
                ["referer"] = "https://page.example.invalid/"
            }
        };
        Assert.True(LocalServiceSoakTrust.ShouldSkipClientCdnProbe(response));

        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment));
        cdn.Add(TikTokSegment, "image/png", PlainPng());

        var probe = new StreamHealthProbe { RequestHeaders = response.RequestHeaders };
        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/", probe);

        // Soak evidence is present, but bytes say "image": rejected.
        Assert.Equal(StreamHealthStatus.SegmentUnreachable, result.Status);
        // And the byte probe itself carried the soak headers (never bare).
        Assert.Contains(cdn.Requests, r => r.Url == TikTokSegment && r.Cookie == "cf=1");
    }

    [Fact]
    public async Task SoakedWrappedTs_IsHealthy_AndProbedWithSoakHeaders()
    {
        var cdn = new Cdn { RequireCookie = "cf=1" };
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(TikTokSegment));
        cdn.Add(TikTokSegment, "image/png", WrappedPngBuilder.Wrap(RealTsOrFake()));

        var probe = new StreamHealthProbe
        {
            RequestHeaders = new Dictionary<string, string> { ["cookie"] = "cf=1" }
        };
        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/", probe);

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task PlainTsStream_StaysHealthy()
    {
        const string segment = "https://cdn.example.invalid/live/seg001.ts";
        var cdn = new Cdn();
        cdn.Add(Manifest, "application/vnd.apple.mpegurl", Playlist(segment));
        cdn.Add(segment, "video/mp2t", WrappedPngBuilder.FakeTs(20));

        var result = await Checker(cdn).CheckStreamHealthAsync(Manifest, "https://page.example.invalid/");

        Assert.Equal(StreamHealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Classification_PlayableKinds_AreExactlyTsFmp4AndWrappedTs()
    {
        var playable = Enum.GetValues<SegmentPayloadKind>().Where(SegmentPayload.IsPlayable).ToArray();

        Assert.Equal(
            new[] { SegmentPayloadKind.MpegTs, SegmentPayloadKind.Fmp4, SegmentPayloadKind.WrappedTs },
            playable);
    }

    private static async Task<(byte[] Bytes, bool Unwrapped)> PlayAsync(PlayerPath path, byte[] body)
    {
        switch (path)
        {
            case PlayerPath.WholeBody:
                {
                    var bytes = SegmentPayload.NormalizeForPlayback(body, out var unwrapped);
                    return (bytes, unwrapped);
                }
            case PlayerPath.PullStream:
                {
                    var source = new MemoryStream(body);
                    var read = WrappedSegmentReader.Read((b, o, c) => source.Read(b, o, c));
                    return (Drain(read, source), read.Unwrapped);
                }
            default:
                {
                    var source = new MemoryStream(body);
                    var read = await WrappedSegmentReader.ReadAsync(source);
                    return (Drain(read, source), read.Unwrapped);
                }
        }
    }

    /// <summary>What the player finally sees: replayed bytes plus the rest of upstream.</summary>
    private static byte[] Drain(WrappedSegmentRead read, System.IO.Stream rest)
    {
        if (read.Unwrapped || read.UpstreamExhausted)
            return read.Body;
        using var ms = new MemoryStream();
        ms.Write(read.Body);
        rest.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] RealTsOrFake()
    {
        try { return WrappedPngBuilder.Fixture("tiktikpx-real-40pkt-mpegts.bin"); }
        catch { return WrappedPngBuilder.FakeTs(40); }
    }

    private static byte[] PlainPng() =>
        WrappedPngBuilder.Png(Enumerable.Range(0, 96 * 96 * 3).Select(i => (byte)(i * 7)).ToArray(), 96, 2);

    private static byte[] Playlist(params string[] segments) =>
        System.Text.Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n" + string.Concat(segments.Select(s => $"#EXTINF:5.76,\n{s}\n")));

    private static StreamHealthChecker Checker(Cdn cdn) =>
        new(new HttpClient(cdn), NullLogger<StreamHealthChecker>.Instance, Options.Create(new StreamHealthSettings { ManifestTimeoutSeconds = 5, SegmentTimeoutSeconds = 5, MaxParallelStreams = 2 }));

    private sealed record Seen(string Method, string Url, string? Range, string? Cookie);

    /// <summary>Fake CDN that honours Range and (optionally) requires a soak cookie.</summary>
    private sealed class Cdn : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Type, byte[] Body)> _objects = new();
        public List<Seen> Requests { get; } = [];
        public string? RequireCookie { get; init; }

        public void Add(string url, string type, byte[] body) => _objects[url] = (type, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : null;
            var range = request.Headers.Range?.ToString();
            lock (Requests)
                Requests.Add(new Seen(request.Method.Method, url, range, cookie));

            if (!_objects.TryGetValue(url, out var obj))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (RequireCookie is not null && cookie != RequireCookie)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

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