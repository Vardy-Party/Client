using System.Net;
using System.Text;
using VardyParty.Linux.Services;
using VardyParty.Streaming.Tests;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxHlsLocalProxyTests
{
    [Fact]
    public async Task Wrap_ServesRewrittenPlaylistAndSegmentBytes()
    {
        var handler = new ScriptedHandler();
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());
        proxy.SetPlaybackHeaders(
            new Dictionary<string, string> { ["Referer"] = "https://page.example/" },
            "https://page.example/");

        var local = proxy.Wrap("https://cdn.example/live.m3u8");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var playlist = await client.GetStringAsync(local);

        Assert.StartsWith("#EXTM3U", playlist.TrimStart());
        Assert.DoesNotContain("cdn.example/seg.ts", playlist);
        Assert.Contains(proxy.Prefix, playlist);

        var segmentLine = playlist.Split('\n').First(l => l.StartsWith(proxy.Prefix, StringComparison.Ordinal));
        var segmentBytes = await client.GetByteArrayAsync(segmentLine.Trim());
        Assert.Equal(new byte[] { 0x47, 0x40, 0x11, 0x10 }, segmentBytes);
        Assert.Equal("https://page.example/", handler.LastReferer);
    }

    [Fact]
    public async Task Wrap_AppliesTransformThenSubstitutesRewrittenSegments()
    {
        var handler = new ScriptedHandler();
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());
        proxy.SetPlaylistTransform(text => text.Replace(
            "decoy.json?_ctump=1",
            "https://kdns.fr/from-plugin.flac?_s2=1",
            StringComparison.Ordinal));
        proxy.SetRewrittenSegments(["https://kdns.fr/from-capture.flac?_s2=2"]);

        var local = proxy.Wrap("https://cdn.example/ctu.m3u8");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var playlist = await client.GetStringAsync(local);

        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith(proxy.Prefix, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, lines.Count);
        Assert.True(LinuxHlsPlaylistRewriter.TryDecodeTarget(lines[0][proxy.Prefix.Length..].Trim(), out var first));
        Assert.True(LinuxHlsPlaylistRewriter.TryDecodeTarget(lines[1][proxy.Prefix.Length..].Trim(), out var second));
        Assert.Equal("https://kdns.fr/from-plugin.flac?_s2=1", first);
        Assert.Equal("https://kdns.fr/from-capture.flac?_s2=2", second);
        Assert.DoesNotContain("decoy.json", playlist);
        Assert.DoesNotContain("left.json", playlist);
    }

    [Fact]
    public async Task Segment_PngWrappedTs_IsUnwrappedForLibVlc()
    {
        var ts = WrappedPngBuilder.FakeTs(400);
        var handler = new ScriptedHandler { WrappedSegment = WrappedPngBuilder.Wrap(ts) };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(SegmentThroughProxy(proxy, "https://p16.tiktokcdn-us.com/a~tplv-x.image"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp2t", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ts, body);
    }

    [Fact]
    public async Task Segment_WebpExifWrappedTs_IsUnwrappedForLibVlc()
    {
        var ts = WrappedPngBuilder.FakeTs(400);
        var handler = new ScriptedHandler { WrappedSegment = WrappedPngBuilder.Webp(ts) };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(SegmentThroughProxy(proxy, "https://p16.tiktokcdn-us.com/a~tplv-tiktokx-origin.image"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp2t", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ts, body);
    }

    [Fact]
    public async Task Segment_PlainPng_PassesThroughUnchanged()
    {
        var png = WrappedPngBuilder.Png(Enumerable.Repeat((byte)0x55, 32 * 32 * 3).ToArray(), 32, 2);
        var handler = new ScriptedHandler { WrappedSegment = png };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(SegmentThroughProxy(proxy, "https://p16.tiktokcdn-us.com/a~tplv-x.image"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, body);
    }

    [Fact]
    public async Task MidPlay_503Then200_RetriesSameUrlAndServesPlaylist()
    {
        var handler = new ScriptedHandler
        {
            PlaylistScript = new Queue<HttpResponseMessage>(
            [
                ScriptedHandler.Text(
                    """
                    #EXTM3U
                    #EXTINF:2.0,
                    seg.ts
                    """),
                ScriptedHandler.Text("<html>503</html>", HttpStatusCode.ServiceUnavailable),
                ScriptedHandler.Text(
                    """
                    #EXTM3U
                    #EXTINF:2.0,
                    recovered.ts
                    """)
            ])
        };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        var local = proxy.Wrap("https://cdn.example/live.m3u8");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var first = await client.GetStringAsync(local);
        Assert.Contains("#EXTM3U", first, StringComparison.Ordinal);

        var second = await client.GetStringAsync(local);
        Assert.Contains("#EXTM3U", second, StringComparison.Ordinal);
        Assert.Equal(3, handler.PlaylistFetchCount);
    }

    [Fact]
    public async Task FirstAttach_503_IsNotRetried()
    {
        var handler = new ScriptedHandler
        {
            PlaylistScript = new Queue<HttpResponseMessage>(
            [
                ScriptedHandler.Text("<html>503</html>", HttpStatusCode.ServiceUnavailable),
                ScriptedHandler.Text("#EXTM3U\n#EXTINF:2.0,\nseg.ts\n")
            ])
        };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        var local = proxy.Wrap("https://cdn.example/live.m3u8");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(local);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.PlaylistFetchCount);
    }

    [Fact]
    public async Task MidPlay_Three503s_GivesUpWith503()
    {
        var handler = new ScriptedHandler
        {
            PlaylistScript = new Queue<HttpResponseMessage>(
            [
                ScriptedHandler.Text("#EXTM3U\n#EXTINF:2.0,\nseg.ts\n"),
                ScriptedHandler.Text("bad", HttpStatusCode.ServiceUnavailable),
                ScriptedHandler.Text("bad", HttpStatusCode.ServiceUnavailable),
                ScriptedHandler.Text("bad", HttpStatusCode.ServiceUnavailable)
            ])
        };
        using var upstream = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var proxy = new LinuxHlsLocalProxy(upstream);
        Assert.True(proxy.TryStart());

        var local = proxy.Wrap("https://cdn.example/live.m3u8");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Assert.StartsWith("#EXTM3U", (await client.GetStringAsync(local)).TrimStart());

        using var failed = await client.GetAsync(local);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(4, handler.PlaylistFetchCount);
    }

    private static string SegmentThroughProxy(LinuxHlsLocalProxy proxy, string target) =>
        LinuxHlsPlaylistRewriter.ToProxyUrl(target, proxy.Prefix);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public byte[]? WrappedSegment { get; init; }

        public Queue<HttpResponseMessage>? PlaylistScript { get; init; }

        public int PlaylistFetchCount { get; private set; }

        public string? LastReferer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastReferer = request.Headers.Referrer?.ToString()
                          ?? (request.Headers.TryGetValues("Referer", out var values)
                              ? values.FirstOrDefault()
                              : null);

            var url = request.RequestUri?.AbsoluteUri ?? "";
            if (PlaylistScript is not null && url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                PlaylistFetchCount++;
                return Task.FromResult(PlaylistScript.Dequeue());
            }

            if (url.EndsWith("ctu.m3u8", StringComparison.Ordinal))
            {
                return Task.FromResult(Text(
                    """
                    #EXTM3U
                    #EXTINF:2.0,
                    decoy.json?_ctump=1
                    #EXTINF:2.0,
                    left.json
                    """));
            }

            if (url.EndsWith("live.m3u8", StringComparison.Ordinal))
            {
                return Task.FromResult(Text(
                    """
                    #EXTM3U
                    #EXTINF:2.0,
                    seg.ts
                    """));
            }

            if (WrappedSegment is not null && url.Contains("tiktokcdn", StringComparison.Ordinal))
            {
                return Task.FromResult(Bytes(WrappedSegment, "image/png"));
            }

            if (url.EndsWith("seg.ts", StringComparison.Ordinal))
            {
                return Task.FromResult(Bytes([0x47, 0x40, 0x11, 0x10], "video/MP2T"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK
                    ? "application/vnd.apple.mpegurl"
                    : "text/html")
            };

        private static HttpResponseMessage Bytes(byte[] body, string type)
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
