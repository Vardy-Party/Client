using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxPlaybackChromeInfoTextTests
{
    [Fact]
    public void FormatVideoInfo_IncludesStreamChannelAndSource()
    {
        // Arrange
        var info = new PlayerOverlayInfo
        {
            Index = 2,
            Total = 5,
            Channel = "Channel North",
            Resolution = "1920x1080",
            AspectRatio = "16:9",
            BitrateKbps = 4500,
            VideoCodec = "H.264",
            M3u8Url = "https://streams.example.com/live.m3u8?token=abc",
            RefererUrl = "https://referer.example.com/page"
        };

        // Act
        var text = LinuxPlaybackChromeInfoText.FormatVideoInfo(info, "Playing");

        // Assert
        Assert.Contains("Status: Playing", text);
        Assert.Contains("Stream: 2/5", text);
        Assert.Contains("Channel: Channel North", text);
        Assert.Contains("Resolution: 1920x1080", text);
        Assert.Contains("Aspect ratio: 16:9", text);
        Assert.Contains("Bitrate: 4500 kbps", text);
        Assert.Contains("Video Codec: H.264", text);
        Assert.Contains("Source: https://streams.example.com/live.m3u8", text);
        Assert.DoesNotContain("token=abc", text);
        Assert.Contains("Referer: referer.example.com", text);
    }

    [Fact]
    public void BuildOverlayInfo_MapsPoolFields()
    {
        // Arrange
        var stream = new Kernel.Stream
        {
            Channel = "Channel North",
            Resolution = "1280x720",
            BitrateKbps = 2500,
            Url = "https://streams.example.com/a"
        };
        var enriched = new EnrichedStream
        {
            Stream = stream,
            ResolvedM3U8Url = "https://cdn.example.com/a.m3u8",
            Referer = "https://referer.example.com",
            Health = new StreamHealth
            {
                Resolution = "1280x720",
                FrameRate = 60,
                VideoCodec = "avc1.4d401f",
                AudioCodec = "mp4a.40.2",
                Bitrate = 2500
            }
        };

        // Act
        var info = LinuxPlaybackChromeInfoText.BuildOverlayInfo(enriched, index: 1, total: 3, refererUrl: enriched.Referer);

        // Assert
        Assert.NotNull(info);
        Assert.Equal(1, info!.Index);
        Assert.Equal(3, info.Total);
        Assert.Equal("Channel North", info.Channel);
        Assert.Equal("1280x720", info.Resolution);
        Assert.Equal(60, info.FrameRate);
        Assert.Equal("H.264", info.VideoCodec);
        Assert.Equal("AAC", info.AudioCodec);
        Assert.Equal("16:9", info.AspectRatio);
        Assert.Equal("https://cdn.example.com/a.m3u8", info.M3u8Url);
    }

    [Fact]
    public void FormatScoresTicker_FiltersSameLeagueInPlay()
    {
        // Arrange
        var games = new[]
        {
            new Game
            {
                Home = "Home United",
                Away = "Away City",
                League = "League Alpha",
                HomeScore = 1,
                AwayScore = 0,
                IsInProgress = true,
                Minute = 67
            },
            new Game
            {
                Home = "North FC",
                Away = "South FC",
                League = "League Beta",
                HomeScore = 2,
                AwayScore = 2,
                IsInProgress = true,
                Minute = 12
            },
            new Game
            {
                Home = "East Town",
                Away = "West Town",
                League = "League Alpha",
                HomeScore = 0,
                AwayScore = 0,
                IsFinished = true
            }
        };

        // Act
        var text = LinuxPlaybackChromeInfoText.FormatScoresTicker(
            games, ScoresTickerMode.SameLeagueInPlay, "League Alpha");

        // Assert
        Assert.Contains("Home United 1-0 Away City", text);
        Assert.DoesNotContain("North FC", text);
        Assert.DoesNotContain("East Town", text);
    }

    [Fact]
    public void FormatScoresTicker_EmptyMode_ReturnsFriendlyEmptyCopy()
    {
        // Arrange
        var games = Array.Empty<Game>();

        // Act
        var text = LinuxPlaybackChromeInfoText.FormatScoresTicker(
            games, ScoresTickerMode.AllFinished, watchedLeague: null);

        // Assert
        Assert.Equal("No finished scores", text);
    }
}

public class LinuxPlaybackChromePlacementTests
{
    [Fact]
    public void TryComputeVideoRowBounds_SubtractsChromeRow()
    {
        // Arrange
        // Act
        var ok = LinuxPlaybackChromePlacement.TryComputeVideoRowBounds(
            hostScreenX: 100,
            hostScreenY: 200,
            hostWidth: 800,
            hostHeight: 600,
            chromeRowHeight: 24,
            out var x,
            out var y,
            out var width,
            out var height);

        // Assert
        Assert.True(ok);
        Assert.Equal(100, x);
        Assert.Equal(224, y);
        Assert.Equal(800, width);
        Assert.Equal(576, height);
    }

    [Fact]
    public void TryComputeVideoRowBounds_RejectsTinyHeight()
    {
        // Arrange
        // Act
        var ok = LinuxPlaybackChromePlacement.TryComputeVideoRowBounds(
            0, 0, 800, 20, chromeRowHeight: 24,
            out _, out _, out _, out _);

        // Assert
        Assert.False(ok);
    }
}
