using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class PlaylistVideoEvidenceTests
{
    [Fact]
    public void Classify_MasterWithResolution_IsVideo()
    {
        // Arrange
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.640028,mp4a.40.2"
            https://cdn.example.com/live/720.m3u8
            """;

        // Act
        var verdict = PlaylistVideoEvidence.Classify(playlist);

        // Assert
        Assert.Equal(PlaylistVideoVerdict.Video, verdict);
    }

    [Fact]
    public void Classify_ZeroResolutionOrAacSegments_IsAudioOnly()
    {
        // Arrange
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=64000,RESOLUTION=0x0,CODECS="mp4a.40.2"
            https://cdn.example.com/live/audio.m3u8
            """;
        const string media = """
            #EXTM3U
            #EXTINF:2.0,
            https://cdn.example.com/seg/loop.aac
            """;

        // Act
        var fromMaster = PlaylistVideoEvidence.Classify(master);
        var fromMedia = PlaylistVideoEvidence.Classify(media);

        // Assert
        Assert.Equal(PlaylistVideoVerdict.AudioOnly, fromMaster);
        Assert.Equal(PlaylistVideoVerdict.AudioOnly, fromMedia);
    }

    [Fact]
    public void Classify_TransportStreamPlaylist_IsVideo()
    {
        // Arrange
        const string playlist = """
            #EXTM3U
            #EXTINF:2.0,
            https://cdn.example.com/seg/1.ts
            """;

        // Act
        var verdict = PlaylistVideoEvidence.Classify(playlist);

        // Assert
        Assert.Equal(PlaylistVideoVerdict.Video, verdict);
    }

    [Fact]
    public void Classify_MissingPlaylist_IsUnverified()
    {
        // Arrange
        const string body = "not a playlist";

        // Act
        var verdict = PlaylistVideoEvidence.Classify(body);

        // Assert
        Assert.Equal(PlaylistVideoVerdict.Unverified, verdict);
    }
}
