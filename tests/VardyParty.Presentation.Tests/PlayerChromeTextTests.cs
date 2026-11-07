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
    [InlineData(1, 3, "720p", "Stream: 1/3")] // resolution/bitrate stay off the switch toast
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
    public void FormatMeasuredQuality_UsesPictureSizeAndMeasuredBitrate()
    {
        // Arrange
        // Act
        var fromPlayback = PlayerChromeText.FormatMeasuredQuality("960x540", 1840, "Unknown Quality");
        var subMbps = PlayerChromeText.FormatMeasuredQuality("960x540", 500, "Unknown Quality");
        var sizeOnly = PlayerChromeText.FormatMeasuredQuality("960x540", null, "Unknown Quality");
        var playlist = PlayerChromeText.FormatMeasuredQuality(null, null, "1080p 60fps 5000kbps");

        // Assert
        Assert.Equal("540p 1.8 Mbps", fromPlayback);
        Assert.Equal("540p 0.5 Mbps", subMbps);
        Assert.Equal("0.0 Mbps", PlayerChromeText.FormatBitrateMbps(3));
        Assert.Equal("540p", sizeOnly);
        Assert.Equal("1080p 60fps 5000kbps", playlist);
    }

    [Fact]
    public void TransferMeter_PublishesBitrateAfterTwoSecondsOfBytes()
    {
        // Arrange
        var meter = new PlaybackTransferMeter();

        // Act
        meter.Add(1, 0);
        meter.Add(1, 1000);
        var early = meter.BitrateKbps;
        meter.Add(500_000, 2000);

        // Assert
        Assert.Null(early);
        Assert.Equal(2000, meter.BitrateKbps);
    }

    [Fact]
    public void TransferMeter_IgnoresPlaylistNoise_ThenAveragesVideoWindows()
    {
        // Arrange
        var meter = new PlaybackTransferMeter();

        // Act
        meter.Add(750, 0);
        meter.Add(1, 2000);
        var noise = meter.BitrateKbps;
        meter.Add(392_750, 2000);
        meter.Add(1, 4000);
        var first = meter.BitrateKbps;
        meter.Add(400_000, 4000);
        meter.Add(1, 6000);

        // Assert
        Assert.Null(noise);
        Assert.Equal(1571, first);
        Assert.Equal(1585, meter.BitrateKbps);
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
            SourceLabel = "V1",
            Quality = "1080p 60fps",
            Resolution = "1920x1080",
            FrameRate = "60 fps",
            AspectRatio = "16:9",
            Bitrate = "4500 kbps",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            Title = "Home United vs Away City",
            Buffer = "Buffer 100%",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var text = PlayerChromeText.FormatVideoInfo(model);

        // Assert — Android panel order: title, status, stream, source badge, quality, detail
        Assert.StartsWith("Home United vs Away City", text);
        Assert.Contains("Status: Playing", text);
        Assert.Contains("Stream: 2/5", text);
        Assert.Contains("Source: V1", text);
        Assert.Contains("1080p 60fps", text);
        Assert.DoesNotContain("Quality:", text);
        Assert.DoesNotContain("Resolution:", text);
        Assert.DoesNotContain("Bitrate:", text);
        Assert.Contains("Channel: Channel North", text);
        Assert.Contains("Aspect ratio: 16:9", text);
        Assert.Contains("Video: H.264", text);
        Assert.Contains("Audio: AAC", text);
        Assert.Contains("Buffer 100%", text);
        Assert.Contains("Source: https://streams.example.com/live.m3u8", text);
        Assert.Contains("Referer: referer.example.com", text);
    }

    [Fact]
    public void FormatVideoInfo_WhileSwitching_KeepsBadgeOmitsTelemetryAndUrls()
    {
        // Arrange — known identity stays; measured size/bitrate/URLs go until the new stream settles
        var model = new VideoInfoPanelModel
        {
            PlaybackState = "Playing",
            StreamIndex = 3,
            StreamTotal = 5,
            Channel = "Channel North",
            SourceLabel = "V1",
            Quality = "540p 1.8 Mbps",
            Resolution = "960x540",
            FrameRate = "30 fps",
            AspectRatio = "16:9",
            Bitrate = "1.8 Mbps",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            Title = "Home United vs Away City",
            Buffer = "80%",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var text = PlayerChromeText.FormatVideoInfo(model, whileSwitching: true);

        // Assert
        Assert.Contains("Status: Switching", text);
        Assert.DoesNotContain("Status: Playing", text);
        Assert.Contains("Stream: 3/5", text);
        Assert.Contains("Channel: Channel North", text);
        Assert.Contains("Source: V1", text);
        Assert.Contains("Home United vs Away City", text);
        Assert.DoesNotContain("540p 1.8 Mbps", text);
        Assert.DoesNotContain("Aspect ratio:", text);
        Assert.DoesNotContain("Video:", text);
        Assert.DoesNotContain("https://streams.example.com", text);
        Assert.DoesNotContain("Referer:", text);
        Assert.DoesNotContain("Buffer ", text);
    }

    [Theory]
    [InlineData("Channel North", "Channel: Channel North")]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    public void FormatVideoInfoDetail_WhileSwitching_ReturnsOnlyChannel(string? channel, string expected)
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            Channel = channel,
            AspectRatio = "16:9",
            Buffer = "Buffer 80%",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var detail = PlayerChromeText.FormatVideoInfoDetail(model, whileSwitching: true);

        // Assert
        Assert.Equal(expected, detail);
    }

    [Theory]
    [InlineData(2, 5, "Stream: 2/5")]
    [InlineData(0, 0, "")]
    [InlineData(3, 0, "")]
    public void FormatStreamIndexLine_IsEmptyWithoutHealthyStreams(int index, int total, string expected)
    {
        // Arrange
        var model = new VideoInfoPanelModel { StreamIndex = index, StreamTotal = total };

        // Act
        var line = PlayerChromeText.FormatStreamIndexLine(model);

        // Assert
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData("Playing", false, "Status: Playing")]
    [InlineData("Playing", true, "Status: Switching")]
    [InlineData(null, false, "Status: unknown")]
    [InlineData(" ", false, "Status: unknown")]
    public void FormatStatusLine_SwitchingOverridesStateAndBlankIsUnknown(
        string? playbackState,
        bool whileSwitching,
        string expected)
    {
        // Arrange
        var model = new VideoInfoPanelModel { PlaybackState = playbackState };

        // Act
        var line = PlayerChromeText.FormatStatusLine(model, whileSwitching);

        // Assert
        Assert.Equal(expected, line);
    }

    [Fact]
    public void FromPlayback_WithoutOverlayCodecs_UsesHealthCodecs()
    {
        // Arrange
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.VideoCodec, "avc1.64001f")
            .With(h => h.AudioCodec, "mp4a.40.2")
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(s => s.Health, health)
            .Create();
        var overlay = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.VideoCodec, (string?)null)
            .With(i => i.AudioCodec, (string?)null)
            .Create();

        // Act
        var model = PlayerChromeText.FromPlayback(
            overlay,
            enriched,
            playbackState: "Playing",
            title: "Home United vs Away City",
            sourceUrl: null,
            refererUrl: null);

        // Assert
        Assert.Equal("H.264", model.VideoCodec);
        Assert.Equal("AAC", model.AudioCodec);
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
        Assert.Equal("V1", model.SourceLabel);
        Assert.Equal("Home United vs Away City", model.Title);
        Assert.Equal("https://streams.example.com/live.m3u8", model.SourceUrl);
        Assert.Equal("referer.example.com", model.RefererHost);
        Assert.Equal("2.5 Mbps", model.Bitrate);
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
        Assert.Equal("Buffer 72%", model.Buffer);
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
        var v1 = PlayerChromeText.IsFacebookSource("v1");
        var legacy = PlayerChromeText.IsFacebookSource("fb");
        var other = PlayerChromeText.IsFacebookSource("V2");

        // Assert
        Assert.True(v1);
        Assert.True(legacy);
        Assert.False(other);
    }
}
