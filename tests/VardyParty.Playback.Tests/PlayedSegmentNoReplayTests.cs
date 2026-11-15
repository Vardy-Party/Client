using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// No-replay rule shared by Android, Windows and Linux: a same-URL reattach (lone
/// CDN-unreachable row, lap with every other row tried, transient error, health-decline
/// reload, token reject) or a switch-back serves only segments the viewer has not seen.
/// </summary>
public class PlayedSegmentNoReplayTests
{
    private const string PlaylistUrl = "https://cdn.example.com/live/home-united/index.m3u8";

    private static string MediaPlaylist(int firstSequence, int count)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n");
        builder.Append("#EXT-X-MEDIA-SEQUENCE:").Append(firstSequence).Append('\n');
        for (var i = 0; i < count; i++)
        {
            builder.Append("#EXTINF:6.0,\n");
            builder.Append("seg").Append(firstSequence + i).Append(".ts?token=fresh\n");
        }

        return builder.ToString();
    }

    private static async Task<string> ServeAsync(PlaylistReloadSession session, string body)
    {
        var request = PlaylistReloadRequest.MidPlay(
            isFirstAttach: !session.FirstPlaylistCompleted,
            session,
            "Home United",
            log: (_, _) => { },
            playlistUrl: PlaylistUrl);
        var outcome = await PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            request,
            _ => Task.FromResult(PlaylistReloadFetchResult.FromHttp(200, Encoding.UTF8.GetBytes(body), "application/vnd.apple.mpegurl")),
            CancellationToken.None);
        Assert.Equal(PlaylistReloadOutcomeKind.Serve, outcome.Kind);
        session.NoteServed();
        return Encoding.UTF8.GetString(outcome.Body!);
    }

    [Fact]
    public async Task Reattach_SameUrl_FirstThreeSegmentsPlayed_ServesOnlyUnplayedSegments()
    {
        // Arrange
        var controller = new PlaybackSessionController();
        for (var i = 100; i < 103; i++)
            controller.PlayedSegments.NoteSegment($"https://cdn.example.com/live/home-united/seg{i}.ts?token=old");
        var reattach = new PlaylistReloadSession(row: "Home United") { PlayedSegments = controller.PlayedSegments };

        // Act
        var served = await ServeAsync(reattach, MediaPlaylist(100, 5));

        // Assert
        Assert.DoesNotContain("seg100.ts", served, StringComparison.Ordinal);
        Assert.DoesNotContain("seg101.ts", served, StringComparison.Ordinal);
        Assert.DoesNotContain("seg102.ts", served, StringComparison.Ordinal);
        Assert.Contains("seg103.ts", served, StringComparison.Ordinal);
        Assert.Contains("seg104.ts", served, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:103", served, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reattach_EverySegmentPlayed_KeepsOnlyTheLiveEdgeSegment()
    {
        // Arrange
        var memory = new PlayedSegmentMemory();
        for (var i = 100; i < 104; i++)
            memory.NoteSegment($"https://cdn.example.com/live/home-united/seg{i}.ts");
        var reattach = new PlaylistReloadSession { PlayedSegments = memory };

        // Act
        var served = await ServeAsync(reattach, MediaPlaylist(100, 4));

        // Assert
        Assert.DoesNotContain("seg102.ts", served, StringComparison.Ordinal);
        Assert.Contains("seg103.ts", served, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameAttach_LaterReload_IsNotTrimmedUnlessArmed()
    {
        // Arrange
        var memory = new PlayedSegmentMemory();
        var attach = new PlaylistReloadSession { PlayedSegments = memory };
        await ServeAsync(attach, MediaPlaylist(100, 3));
        memory.NoteSegment("https://cdn.example.com/live/home-united/seg100.ts");

        // Act
        var unarmed = await ServeAsync(attach, MediaPlaylist(100, 3));
        memory.ArmTrimOnce();
        var armed = await ServeAsync(attach, MediaPlaylist(100, 3));

        // Assert
        Assert.Contains("seg100.ts", unarmed, StringComparison.Ordinal);
        Assert.DoesNotContain("seg100.ts", armed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MasterPlaylist_PassesThrough_AndFirstVariantIsStillTrimmed()
    {
        // Arrange
        var memory = new PlayedSegmentMemory();
        memory.NoteSegment("https://cdn.example.com/live/home-united/seg100.ts");
        var attach = new PlaylistReloadSession { PlayedSegments = memory };
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nvariant.m3u8\n";

        // Act
        var servedMaster = await ServeAsync(attach, master);
        var servedVariant = await ServeAsync(attach, MediaPlaylist(100, 3));

        // Assert
        Assert.Equal(master, servedMaster);
        Assert.DoesNotContain("seg100.ts", servedVariant, StringComparison.Ordinal);
        Assert.Contains("seg101.ts", servedVariant, StringComparison.Ordinal);
    }

    [Fact]
    public void NoteSegment_IgnoresPlaylists_AndMatchesWithoutSignedQuery()
    {
        // Arrange
        var memory = new PlayedSegmentMemory();

        // Act
        memory.NoteSegment("https://cdn.example.com/live/home-united/index.m3u8?token=a");
        memory.NoteSegment("https://cdn.example.com/live/home-united/seg7.ts?token=a");

        // Assert
        Assert.Equal(1, memory.Count);
        Assert.True(memory.WasPlayed("https://cdn.example.com/live/home-united/seg7.ts?token=b"));
        Assert.False(memory.WasPlayed("https://cdn.example.com/live/away-city/seg7.ts"));
    }

    [Fact]
    public void NoteSegment_ForgetsOldestBeyondMaxIds()
    {
        // Arrange
        var memory = new PlayedSegmentMemory();

        // Act
        for (var i = 0; i <= PlayedSegmentMemory.MaxIds; i++)
            memory.NoteSegment($"https://cdn.example.com/live/home-united/seg{i}.ts");

        // Assert
        Assert.Equal(PlayedSegmentMemory.MaxIds, memory.Count);
        Assert.False(memory.WasPlayed("https://cdn.example.com/live/home-united/seg0.ts"));
        Assert.True(memory.WasPlayed($"https://cdn.example.com/live/home-united/seg{PlayedSegmentMemory.MaxIds}.ts"));
    }
}
