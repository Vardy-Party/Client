using System;
using System.Linq;
using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

/// <summary>
/// The Linux LibVLC loopback proxy must resolve root-relative and protocol-relative
/// playlist URIs (segments, variants, EXT-X-KEY / EXT-X-MAP) the same way health and
/// the other platform players do, never as a local file path.
/// </summary>
public class LinuxHlsPlaylistRewriterRootRelativeTests
{
    private const string Prefix = "http://127.0.0.1:9/u/";

    [Fact]
    public void Rewrite_ResolvesRootRelativeSegmentsKeysAndMaps()
    {
        const string playlist =
            """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="/keys/1.key?k=2"
            #EXT-X-MAP:URI="//init.example/init.mp4"
            #EXTINF:6.0,
            /hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9
            """;

        var rewritten = LinuxHlsPlaylistRewriter.Rewrite(
            playlist,
            "https://hls.example.invalid/live/abc/index.m3u8?t=1",
            Prefix);

        var targets = DecodeAll(rewritten);
        Assert.Contains("https://hls.example.invalid/hls-segments/9f8e7d/6195.ts?e=1793478144&s=Zx9", targets);
        Assert.Contains("https://hls.example.invalid/keys/1.key?k=2", targets);
        Assert.Contains("https://init.example/init.mp4", targets);
        Assert.DoesNotContain(targets, t => t.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rewrite_ResolvesRootRelativeVariant()
    {
        const string playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000
            /live/abc/720/index.m3u8?t=1
            """;

        var rewritten = LinuxHlsPlaylistRewriter.Rewrite(playlist, "https://hls.example.invalid/live/abc/master.m3u8", Prefix);

        Assert.Equal(["https://hls.example.invalid/live/abc/720/index.m3u8?t=1"], DecodeAll(rewritten));
    }

    [Fact]
    public void Rewrite_RejectsNonHttpPlaylistUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            LinuxHlsPlaylistRewriter.Rewrite("#EXTM3U\n", "/live/index.m3u8", Prefix));
    }

    private static string[] DecodeAll(string rewritten)
    {
        var tokens = rewritten
            .Split(Prefix, StringSplitOptions.None)
            .Skip(1)
            .Select(part => new string(part.TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray()));
        return tokens
            .Select(t => LinuxHlsPlaylistRewriter.TryDecodeTarget(t, out var url) ? url : "<undecodable>")
            .ToArray();
    }
}
