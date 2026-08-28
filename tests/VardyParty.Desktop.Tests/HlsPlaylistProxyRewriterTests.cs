using VardyParty.Desktop.Services;
using Xunit;

namespace VardyParty.Desktop.Tests;

public class HlsPlaylistProxyRewriterTests
{
    [Fact]
    public void Rewrite_ResolvesRelativeSegmentsAndQuotedKeyUri()
    {
        // Arrange
        const string playlist =
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-KEY:METHOD=AES-128,URI="key.key"
            #EXTINF:2.0,
            seg0.ts
            #EXTINF:2.0,
            https://cdn.example/abs.ts
            """;
        var baseUri = new Uri("https://origin.example/live/index.m3u8");

        // Act
        var rewritten = HlsPlaylistProxyRewriter.Rewrite(
            playlist,
            baseUri,
            absolute => "http://127.0.0.1:9/u?u=" + Uri.EscapeDataString(absolute));

        // Assert
        Assert.Contains(
            "URI=\"http://127.0.0.1:9/u?u=" + Uri.EscapeDataString("https://origin.example/live/key.key") + "\"",
            rewritten);
        Assert.Contains(
            "http://127.0.0.1:9/u?u=" + Uri.EscapeDataString("https://origin.example/live/seg0.ts"),
            rewritten);
        Assert.Contains(
            "http://127.0.0.1:9/u?u=" + Uri.EscapeDataString("https://cdn.example/abs.ts"),
            rewritten);
        Assert.DoesNotContain("\nseg0.ts\n", rewritten);
    }

    [Fact]
    public void Rewrite_RewritesMasterPlaylistVariants()
    {
        // Arrange
        const string playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            720p.m3u8
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",URI='audio.m3u8'
            """;
        var baseUri = new Uri("https://origin.example/master.m3u8");

        // Act
        var rewritten = HlsPlaylistProxyRewriter.Rewrite(
            playlist,
            baseUri,
            absolute => "proxy:" + absolute);

        // Assert
        Assert.Contains("proxy:https://origin.example/720p.m3u8", rewritten);
        Assert.Contains("URI='proxy:https://origin.example/audio.m3u8'", rewritten);
    }

    [Fact]
    public void Rewrite_LeavesNonHttpSchemesAlone()
    {
        // Arrange
        const string playlist =
            """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://license-key"
            #EXTINF:1,
            data:application/octet-stream;base64,AAAA
            """;

        // Act
        var rewritten = HlsPlaylistProxyRewriter.Rewrite(
            playlist,
            new Uri("https://origin.example/a.m3u8"),
            _ => throw new InvalidOperationException("should not proxy"));

        // Assert
        Assert.Contains("URI=\"skd://license-key\"", rewritten);
        Assert.Contains("data:application/octet-stream;base64,AAAA", rewritten);
    }
}
