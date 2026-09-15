using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxHlsPlaylistRewriterTests
{
    private const string Prefix = "http://127.0.0.1:9/u/";

    [Fact]
    public void LooksLikePlaylist_RequiresExtm3u()
    {
        Assert.True(LinuxHlsPlaylistRewriter.LooksLikePlaylist("#EXTM3U\n#EXTINF:1,\nseg.ts\n"));
        Assert.True(LinuxHlsPlaylistRewriter.LooksLikePlaylist("\n  #EXTM3U\n"));
        Assert.False(LinuxHlsPlaylistRewriter.LooksLikePlaylist("GIF89a"));
        Assert.True(LinuxHlsPlaylistRewriter.LooksLikePlaylist("#EXTM3U"u8.ToArray()));
        Assert.False(LinuxHlsPlaylistRewriter.LooksLikePlaylist("not a playlist"u8.ToArray()));
    }

    [Fact]
    public void Rewrite_RewritesRelativeSegmentsAndKeyUri()
    {
        const string playlist =
            """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="keys/1.key"
            #EXTINF:4.0,
            ts77.seg.json?_s2=abc
            """;

        var rewritten = LinuxHlsPlaylistRewriter.Rewrite(
            playlist,
            "https://cdn.example/live/index.m3u8",
            Prefix);

        Assert.Contains("#EXTM3U", rewritten);
        Assert.DoesNotContain("cdn.example", rewritten);
        Assert.Contains(Prefix, rewritten);
        Assert.True(LinuxHlsPlaylistRewriter.TryDecodeTarget(
            ExtractToken(rewritten, "ts77"),
            out var segment));
        Assert.Equal("https://cdn.example/live/ts77.seg.json?_s2=abc", segment);
        Assert.True(LinuxHlsPlaylistRewriter.TryDecodeTarget(
            ExtractToken(rewritten, "keys"),
            out var key));
        Assert.Equal("https://cdn.example/live/keys/1.key", key);
    }

    [Fact]
    public void Rewrite_RewritesMasterVariantLine()
    {
        const string playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000
            https://cdn.example/hi/index.m3u8
            """;

        var rewritten = LinuxHlsPlaylistRewriter.Rewrite(
            playlist,
            "https://cdn.example/master.m3u8",
            Prefix);

        Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=2000000", rewritten);
        Assert.True(LinuxHlsPlaylistRewriter.TryDecodeTarget(
            ExtractToken(rewritten, "hi"),
            out var variant));
        Assert.Equal("https://cdn.example/hi/index.m3u8", variant);
    }

    [Fact]
    public void SubstitutePlayableSegments_ReplacesDecoyJsonWithCapturedUrls()
    {
        const string playlist =
            """
            #EXTM3U
            #EXTINF:4.0,
            1789245121946.json?_ctump=abc
            #EXTINF:4.0,
            https://kdns.fr/keep.flac?_s2=1
            """;

        var rewritten = LinuxHlsPlaylistRewriter.SubstitutePlayableSegments(
            playlist,
            ["https://sylvia.isyjvux.kdns.fr/cfall/seg.flac?_s2=aa"]);

        Assert.Contains("https://sylvia.isyjvux.kdns.fr/cfall/seg.flac?_s2=aa", rewritten);
        Assert.DoesNotContain("1789245121946.json", rewritten);
        Assert.Contains("https://kdns.fr/keep.flac?_s2=1", rewritten);
    }

    [Fact]
    public void SubstitutePlayableSegments_LeavesMasterPlaylistAlone()
    {
        const string playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000
            index.m3u8
            """;

        var rewritten = LinuxHlsPlaylistRewriter.SubstitutePlayableSegments(
            playlist,
            ["https://cdn.example/seg.ts"]);

        Assert.Contains("index.m3u8", rewritten);
        Assert.DoesNotContain("cdn.example/seg.ts", rewritten);
    }

    [Fact]
    public void TryDecodeTarget_RejectsNonHttp()
    {
        var token = LinuxHlsPlaylistRewriter.EncodeTarget("file:///etc/passwd");
        Assert.False(LinuxHlsPlaylistRewriter.TryDecodeTarget(token, out _));
    }

    private static string ExtractToken(string rewritten, string hint)
    {
        foreach (var line in rewritten.Split('\n'))
        {
            var idx = line.IndexOf(Prefix, StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var token = line[(idx + Prefix.Length)..].Trim().TrimEnd('"');
            if (LinuxHlsPlaylistRewriter.TryDecodeTarget(token, out var url)
                && url.Contains(hint, StringComparison.Ordinal))
            {
                return token;
            }
        }

        throw new InvalidOperationException($"No proxied URI containing '{hint}'.");
    }
}
