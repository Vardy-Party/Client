using System.Net;
using System.Text;
using VardyParty.Linux.Services;
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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
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

            if (url.EndsWith("seg.ts", StringComparison.Ordinal))
            {
                return Task.FromResult(Bytes([0x47, 0x40, 0x11, 0x10], "video/MP2T"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/vnd.apple.mpegurl")
            };

        private static HttpResponseMessage Bytes(byte[] body, string type)
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
