using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class PlayerChromeTextTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Theory]
    [InlineData(2, 5, null, "Stream: 2/5")]
    [InlineData(1, 3, "720p", "Stream: 1/3 (720p)")]
    [InlineData(0, 0, null, "Streams: 0")]
    public void FormatStreamCount_MatchesWindowsAndroidToast(int index, int total, string? res, string expected)
    {
        // Arrange
        // Act
        var text = PlayerChromeText.FormatStreamCount(index, total, res);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("1920x1080", "1080p")]
    [InlineData("1280X720", "720p")]
    [InlineData("1080p", "1080p")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractVerticalResolution_ParsesPairOrPassthrough(string? input, string? expected)
    {
        // Arrange
        // Act
        var result = PlayerChromeText.ExtractVerticalResolution(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatVideoInfo_IncludesStreamChannelAndSource()
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            PlaybackState = "Playing",
            StreamIndex = 2,
            StreamTotal = 5,
            Channel = "Channel North",
            SourceLabel = "FB",
            Quality = "1080p 60fps",
            Resolution = "1920x1080",
            FrameRate = "60 fps",
            AspectRatio = "16:9",
            Bitrate = "4500 kbps",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            Title = "Home United vs Away City",
            Buffer = "100%",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var text = PlayerChromeText.FormatVideoInfo(model);

        // Assert
        Assert.Contains("Status: Playing", text);
        Assert.Contains("Stream: 2/5", text);
        Assert.Contains("Channel: Channel North", text);
        Assert.Contains("Source: FB", text);
        Assert.Contains("Quality: 1080p 60fps", text);
        Assert.Contains("Resolution: 1920x1080 @ 60 fps", text);
        Assert.Contains("Aspect ratio: 16:9", text);
        Assert.Contains("Bitrate: 4500 kbps", text);
        Assert.Contains("Video Codec: H.264", text);
        Assert.Contains("Audio Codec: AAC", text);
        Assert.Contains("Home United vs Away City", text);
        Assert.Contains("Buffer: 100%", text);
        Assert.Contains("Source: https://streams.example.com/live.m3u8", text);
        Assert.Contains("Referer: referer.example.com", text);
    }

    [Fact]
    public void StripQuery_RemovesQueryString()
    {
        // Arrange
        const string url = "https://streams.example.com/live.m3u8?token=secret";

        // Act
        var stripped = PlayerChromeText.StripQuery(url);

        // Assert
        Assert.Equal("https://streams.example.com/live.m3u8", stripped);
        Assert.DoesNotContain("token", stripped);
    }

    [Fact]
    public void FromPlayback_PrefersOverlayThenCurrentStream()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Channel, "Channel North")
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.Source, "fb")
            .With(s => s.Url, "https://streams.example.com/watch")
            .With(s => s.Resolution, "1280x720")
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(s => s.Stream, stream)
            .With(s => s.ResolvedM3U8Url, "https://streams.example.com/live.m3u8?x=1")
            .Without(s => s.Health)
            .Create();
        var overlay = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 4)
            .With(i => i.Channel, "Channel North")
            .With(i => i.Resolution, "1280x720")
            .With(i => i.BitrateKbps, 2500)
            .With(i => i.M3u8Url, (string?)null)
            .Create();

        // Act
        var model = PlayerChromeText.FromPlayback(
            overlay,
            enriched,
            playbackState: "Playing",
            title: "Home United vs Away City",
            sourceUrl: null,
            refererUrl: "https://referer.example.com/page");

        // Assert
        Assert.Equal(1, model.StreamIndex);
        Assert.Equal(4, model.StreamTotal);
        Assert.Equal("Channel North", model.Channel);
        Assert.Equal("FB", model.SourceLabel);
        Assert.Equal("Home United vs Away City", model.Title);
        Assert.Equal("https://streams.example.com/live.m3u8", model.SourceUrl);
        Assert.Equal("referer.example.com", model.RefererHost);
        Assert.Equal("2500 kbps", model.Bitrate);
    }

    [Fact]
    public void FromPlayback_MpChip_UsesPlayerStreamNotGameTitleOrBadge()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Channel, "V2")
            .With(s => s.PlayerStream, "BE ID")
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.Url, "https://streams.example.com/page")
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(s => s.Stream, stream)
            .With(s => s.ResolvedM3U8Url, "https://cdn.example.com/live.m3u8")
            .Without(s => s.Health)
            .Create();
        var overlay = PlayerOverlayFormatter.BuildOverlayInfo(enriched, index: 1, total: 3);

        // Act
        var model = PlayerChromeText.FromPlayback(
            overlay,
            enriched,
            playbackState: "Playing",
            title: "Home United vs Away City",
            sourceUrl: null,
            refererUrl: stream.Url,
            bufferPercent: 72);

        // Assert
        Assert.Equal("BE ID", model.Channel);
        Assert.Equal("V2", model.SourceLabel);
        Assert.Equal("Home United vs Away City", model.Title);
        Assert.Equal("72%", model.Buffer);
        Assert.DoesNotContain("V2", model.Channel);
    }

    [Fact]
    public void BuildAspectRatio_ReducesByGcd()
    {
        // Arrange
        // Act
        var ratio = PlayerChromeText.BuildAspectRatio("1920x1080");

        // Assert
        Assert.Equal("16:9", ratio);
    }

    [Fact]
    public void IsFacebookSource_IsCaseInsensitive()
    {
        // Arrange
        // Act
        var fb = PlayerChromeText.IsFacebookSource("fb");
        var other = PlayerChromeText.IsFacebookSource("V2");

        // Assert
        Assert.True(fb);
        Assert.False(other);
    }
}
