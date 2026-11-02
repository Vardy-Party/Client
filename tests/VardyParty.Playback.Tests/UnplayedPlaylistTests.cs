using System;
using System.Collections.Generic;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class UnplayedPlaylistTests
{
    private const string PlaylistUrl = "https://edge.example.test/premium31/index.m3u8?t=1";

    private static readonly string[] Segments =
    [
        "https://cdn.example.test/obj/aaa~origin.image?sig=1",
        "https://cdn.example.test/obj/bbb~origin.image?sig=1",
        "https://cdn.example.test/obj/ccc~origin.image?sig=1"
    ];

    [Fact]
    public void Trim_DropsPlayedPrefix_AndAdvancesMediaSequence()
    {
        // Arrange
        var playlist = Playlist(100, Segments);
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Equal(2, trimmed.Dropped);
        Assert.DoesNotContain("aaa~origin", trimmed.Text);
        Assert.DoesNotContain("bbb~origin", trimmed.Text);
        Assert.Contains("ccc~origin", trimmed.Text);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:102", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_SamePathWithANewQuery_CountsAsPlayed()
    {
        // Arrange
        var playlist = Playlist(40, Segments);
        var played = Identities("https://cdn.example.test/obj/aaa~origin.image?sig=other");

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Equal(1, trimmed.Dropped);
        Assert.DoesNotContain("aaa~origin", trimmed.Text);
        Assert.Contains("bbb~origin", trimmed.Text);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:41", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_NothingPlayed_ReturnsThePlaylist()
    {
        // Arrange
        var playlist = Playlist(7, Segments);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, _ => false, PlaylistUrl);

        // Assert
        Assert.Equal(0, trimmed.Dropped);
        Assert.Same(playlist, trimmed.Text);
    }

    [Fact]
    public void Trim_EntireWindowPlayed_KeepsTheLastSegment()
    {
        // Arrange
        var playlist = Playlist(9, Segments);
        var played = Identities(Segments);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Equal(2, trimmed.Dropped);
        Assert.DoesNotContain("aaa~origin", trimmed.Text);
        Assert.DoesNotContain("bbb~origin", trimmed.Text);
        Assert.Contains("ccc~origin", trimmed.Text);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:11", trimmed.Text, StringComparison.Ordinal);
    }

    private static string Playlist(long sequence, params string[] segments)
    {
        var lines = new List<string>
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:6",
            "#EXT-X-MEDIA-SEQUENCE:" + sequence.ToString()
        };
        foreach (var segment in segments)
        {
            lines.Add("#EXTINF:6.000,");
            lines.Add(segment);
        }

        return string.Join('\n', lines) + "\n";
    }

    private static Func<string, bool> Identities(params string[] urls)
    {
        var played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
            played.Add(UnplayedPlaylist.SegmentIdentity(url));
        return url => played.Contains(UnplayedPlaylist.SegmentIdentity(url));
    }
}
