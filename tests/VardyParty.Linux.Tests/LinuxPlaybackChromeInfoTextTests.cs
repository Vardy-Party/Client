using AutoFixture;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Linux.Services;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxPlaybackChromeInfoTextTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void FormatVideoInfo_IncludesStreamChannelAndSource()
    {
        // Arrange
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 2)
            .With(i => i.Total, 5)
            .With(i => i.Channel, "Channel North")
            .With(i => i.Resolution, "1920x1080")
            .With(i => i.AspectRatio, "16:9")
            .With(i => i.BitrateKbps, 4500)
            .With(i => i.VideoCodec, "H.264")
            .With(i => i.AudioCodec, (string?)null)
            .With(i => i.M3u8Url, "https://streams.example.com/live.m3u8?token=abc")
            .With(i => i.RefererUrl, "https://referer.example.com/page")
            .With(i => i.Title, "Channel North")
            .With(i => i.BufferPercent, (int?)null)
            .Create();

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
        var stream = _fixture.Build<Kernel.Stream>()
            .With(s => s.Channel, "Channel North")
            .With(s => s.Resolution, "1280x720")
            .With(s => s.BitrateKbps, 2500)
            .With(s => s.Url, "https://streams.example.com/a")
            .Create();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Resolution, "1280x720")
            .With(h => h.FrameRate, 60)
            .With(h => h.VideoCodec, "avc1.4d401f")
            .With(h => h.AudioCodec, "mp4a.40.2")
            .With(h => h.Bitrate, 2500)
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/a.m3u8")
            .With(e => e.Referer, "https://referer.example.com")
            .With(e => e.Health, health)
            .Create();

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
        var sameLeagueLive = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, string.Empty)
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.StatusText, string.Empty)
            .With(g => g.HomeScore, 1)
            .With(g => g.AwayScore, 0)
            .With(g => g.IsInProgress, true)
            .With(g => g.IsFinished, false)
            .With(g => g.Minute, 67)
            .Create();
        var otherLeagueLive = _fixture.Build<Game>()
            .With(g => g.Home, "North FC")
            .With(g => g.Away, "South FC")
            .With(g => g.League, "League Beta")
            .With(g => g.BBCLeague, string.Empty)
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.StatusText, string.Empty)
            .With(g => g.HomeScore, 2)
            .With(g => g.AwayScore, 2)
            .With(g => g.IsInProgress, true)
            .With(g => g.IsFinished, false)
            .With(g => g.Minute, 12)
            .Create();
        var sameLeagueFinished = _fixture.Build<Game>()
            .With(g => g.Home, "East Town")
            .With(g => g.Away, "West Town")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, string.Empty)
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.StatusText, "FT")
            .With(g => g.HomeScore, 0)
            .With(g => g.AwayScore, 0)
            .With(g => g.IsFinished, true)
            .With(g => g.IsInProgress, false)
            .Create();
        var games = new[] { sameLeagueLive, otherLeagueLive, sameLeagueFinished };

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

public class LinuxPlaybackFullscreenSessionTests
{
    [Fact]
    public void Toggle_EntersFullScreen_AndRestoresPriorMode()
    {
        // Arrange
        var sut = new LinuxPlaybackFullscreenSession();

        // Act
        var entered = sut.Toggle(LinuxHostWindowMode.Maximized, _ => null);
        var exited = sut.Toggle(LinuxHostWindowMode.FullScreen, _ => null);

        // Assert
        Assert.Equal(LinuxHostWindowMode.FullScreen, entered);
        Assert.False(sut.IsFullscreen);
        Assert.Equal(LinuxHostWindowMode.Maximized, exited);
    }

    [Fact]
    public void ResolveEnterTarget_RespectsMaximizeEnv()
    {
        // Arrange
        // Act
        var mode = LinuxPlaybackFullscreenSession.ResolveEnterTarget(
            name => name == LinuxPlaybackFullscreenSession.MaximizeInsteadEnv ? "1" : null);

        // Assert
        Assert.Equal(LinuxHostWindowMode.Maximized, mode);
    }

    [Fact]
    public void EscapeOrder_ExitsFullscreenBeforeClose()
    {
        // Arrange
        // Act
        var whenFs = LinuxPlaybackEscapeOrder.Next(isFullscreenPlayback: true);
        var whenWindowed = LinuxPlaybackEscapeOrder.Next(isFullscreenPlayback: false);

        // Assert
        Assert.Equal(LinuxPlaybackEscapeAction.ExitFullscreen, whenFs);
        Assert.Equal(LinuxPlaybackEscapeAction.ClosePlayback, whenWindowed);
    }
}
