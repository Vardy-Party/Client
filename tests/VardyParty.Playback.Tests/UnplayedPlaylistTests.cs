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

    [Fact]
    public void Trim_KeyBeforeFirstDroppedSegment_IsCarriedAheadOfFirstKeptSegment()
    {
        // Arrange
        const string key = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.test/k1\",IV=0x1";
        var playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:100\n"
            + key + "\n#EXTINF:6.000,\n" + Segments[0] + "\n#EXTINF:6.000,\n" + Segments[1] + "\n#EXTINF:6.000,\n" + Segments[2] + "\n";
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Equal(2, trimmed.Dropped);
        Assert.Contains(key, trimmed.Text, StringComparison.Ordinal);
        Assert.True(trimmed.Text.IndexOf(key, StringComparison.Ordinal) < trimmed.Text.IndexOf("ccc~origin", StringComparison.Ordinal));
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:102", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_KeyRotatedInsideDroppedSegments_CarriesOnlyTheLastEffectiveKey()
    {
        // Arrange
        const string oldKey = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.test/k1\"";
        const string newKey = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.test/k2\"";
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n"
            + oldKey + "\n#EXTINF:6.000,\n" + Segments[0]
            + "\n" + newKey + "\n#EXTINF:6.000,\n" + Segments[1]
            + "\n#EXTINF:6.000,\n" + Segments[2] + "\n";
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.DoesNotContain(oldKey, trimmed.Text, StringComparison.Ordinal);
        Assert.Contains(newKey, trimmed.Text, StringComparison.Ordinal);
        Assert.True(trimmed.Text.IndexOf(newKey, StringComparison.Ordinal) < trimmed.Text.IndexOf("ccc~origin", StringComparison.Ordinal));
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:9", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_FirstKeptSegmentHasItsOwnKey_DoesNotDuplicateTheDroppedKey()
    {
        // Arrange
        const string oldKey = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.test/k1\"";
        const string ownKey = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.test/k3\"";
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:1\n"
            + oldKey + "\n#EXTINF:6.000,\n" + Segments[0]
            + "\n" + ownKey + "\n#EXTINF:6.000,\n" + Segments[1] + "\n";
        var played = Identities(Segments[0]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.DoesNotContain(oldKey, trimmed.Text, StringComparison.Ordinal);
        Assert.Contains(ownKey, trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_MapInsideDroppedSegments_IsCarriedAheadOfFirstKeptSegment()
    {
        // Arrange
        const string map = "#EXT-X-MAP:URI=\"init-2.mp4\"";
        var playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI=\"init-1.mp4\"\n#EXT-X-MEDIA-SEQUENCE:20\n"
            + "#EXTINF:6.000,\n" + Segments[0]
            + "\n" + map + "\n#EXTINF:6.000,\n" + Segments[1]
            + "\n#EXTINF:6.000,\n" + Segments[2] + "\n";
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Contains(map, trimmed.Text, StringComparison.Ordinal);
        Assert.True(trimmed.Text.IndexOf(map, StringComparison.Ordinal) < trimmed.Text.IndexOf("ccc~origin", StringComparison.Ordinal));
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:22", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_DroppedDiscontinuity_BumpsExistingDiscontinuitySequence()
    {
        // Arrange
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:50\n#EXT-X-DISCONTINUITY-SEQUENCE:4\n"
            + "#EXTINF:6.000,\n" + Segments[0]
            + "\n#EXT-X-DISCONTINUITY\n#EXTINF:6.000,\n" + Segments[1]
            + "\n#EXTINF:6.000,\n" + Segments[2] + "\n";
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:5", trimmed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY\n", trimmed.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:52", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_DroppedDiscontinuity_InsertsDiscontinuitySequenceWhenAbsent()
    {
        // Arrange
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:50\n"
            + "#EXT-X-DISCONTINUITY\n#EXTINF:6.000,\n" + Segments[0]
            + "\n#EXT-X-DISCONTINUITY\n#EXTINF:6.000,\n" + Segments[1]
            + "\n#EXTINF:6.000,\n" + Segments[2] + "\n";
        var played = Identities(Segments[0], Segments[1]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:52\n#EXT-X-DISCONTINUITY-SEQUENCE:2\n", trimmed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_DiscontinuityOnFirstKeptSegment_IsKeptAndNotCounted()
    {
        // Arrange
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:50\n#EXT-X-DISCONTINUITY-SEQUENCE:4\n"
            + "#EXTINF:6.000,\n" + Segments[0]
            + "\n#EXT-X-DISCONTINUITY\n#EXTINF:6.000,\n" + Segments[1] + "\n";
        var played = Identities(Segments[0]);

        // Act
        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(playlist, played, PlaylistUrl);

        // Assert
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:4\n", trimmed.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-DISCONTINUITY\n#EXTINF", trimmed.Text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:51", trimmed.Text, StringComparison.Ordinal);
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
