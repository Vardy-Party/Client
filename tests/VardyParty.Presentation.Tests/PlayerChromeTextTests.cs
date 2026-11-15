using System;
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
    [InlineData(2, 5, null, "2/5")]
    [InlineData(1, 3, "720p", "1/3 · 720p")]
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
    public void FormatMeasuredQuality_UsesPictureSize_NeverBitrate()
    {
        // Arrange
        const string playlistLabel = "1080p 60fps 5000kbps";

        // Act
        var fromPlayback = PlayerChromeText.FormatMeasuredQuality("960x540", "Unknown Quality");
        var playlist = PlayerChromeText.FormatMeasuredQuality(null, playlistLabel);
        var nothing = PlayerChromeText.FormatMeasuredQuality(null, "4500kbps");

        // Assert
        Assert.Equal("540p", fromPlayback);
        Assert.Equal("1080p 60fps", playlist);
        Assert.Equal("Unknown Quality", nothing);
    }

    [Theory]
    [InlineData("720p 3.4 Mbps", "720p")]
    [InlineData("1080p 60fps 5000kbps", "1080p 60fps")]
    [InlineData("0.5 Mbps", "")]
    [InlineData("540p", "540p")]
    public void StripBitrate_RemovesEveryBitrateToken(string input, string expected)
    {
        // Arrange
        // Act
        var result = PlayerChromeText.StripBitrate(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void VideoInfoPanel_ShowsHeightAndBitrateSideBySide_NotNextToStreamName()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Channel, "Channel North")
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.PlayerStreams, [])
            .With(s => s.Source, "fb")
            .With(s => s.BitrateKbps, 5000)
            .Create();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .With(h => h.Resolution, "1920x1080")
            .With(h => h.FrameRate, 60)
            .With(h => h.Bitrate, 5000)
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(s => s.Stream, stream)
            .With(s => s.Status, StreamResolutionStatus.Healthy)
            .With(s => s.Health, health)
            .Create();
        var overlay = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 2)
            .With(i => i.Total, 9)
            .With(i => i.Channel, "Channel North")
            .With(i => i.Resolution, "1280x720")
            .With(i => i.ProbedResolution, (string?)null)
            .With(i => i.BitrateKbps, 3400)
            .Create();

        // Act
        var model = PlayerChromeText.FromPlayback(overlay, enriched, "Playing", "Home United vs Away City", null, null);
        var quality = PlayerChromeText.FormatQualityLine(model);
        var identity = PlayerChromeText.FormatStreamIdentityLine(model, model.SourceLabel);
        var suffix = PlayerChromeText.FormatStreamIdentitySuffix(model);
        var panelLines = PlayerChromeText.FormatVideoInfo(model).Split('\n');

        // Assert — one line carries height and bitrate; the stream-name row carries neither
        Assert.Equal("720p · 3.4 Mbps", quality);
        Assert.Equal("2/9 · V1 · Channel North", identity);
        Assert.Equal("Channel North", suffix);
        Assert.Single(panelLines, line => line.Contains("720p", StringComparison.Ordinal));
        Assert.Single(panelLines, line => line.Contains("Mbps", StringComparison.Ordinal));
        Assert.Contains("720p · 3.4 Mbps", panelLines);
        Assert.DoesNotContain(panelLines, line =>
            line.Contains("Channel North", StringComparison.Ordinal)
            && (line.Contains("720p", StringComparison.Ordinal) || line.Contains("Mbps", StringComparison.Ordinal)));
        foreach (var text in new[]
                 {
                     identity,
                     suffix,
                     PlayerChromeText.FormatStatusLine(model),
                     PlayerChromeText.FormatStreamIdentityIndex(model),
                     PlayerChromeText.FormatVideoInfoDetail(model)
                 })
        {
            Assert.DoesNotContain("bps", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("720p", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(3400, "3.4 Mbps")]
    [InlineData(500, "0.5 Mbps")]
    [InlineData(12049, "12.0 Mbps")]
    public void FormatBitrateMbps_RoundsToOneDecimal(int kbps, string expected)
    {
        // Arrange
        // Act
        var text = PlayerChromeText.FormatBitrateMbps(kbps);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void StreamToast_KeepsHeightButNeverShowsBitrate()
    {
        // Arrange
        var toast = new StreamToastModel(2, 9, "1920x1080", "V2", "Channel North");

        // Act
        var strings = new[]
        {
            toast.Text,
            toast.Parts.Text,
            PlayerChromeText.FormatStreamCount(2, 9, "1920x1080", "V1", "Channel North")
        };

        // Assert
        Assert.Equal("2/9 · V2 · Channel North · 1080p", toast.Text);
        Assert.All(strings, text => Assert.DoesNotContain("bps", text, StringComparison.OrdinalIgnoreCase));
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
            Bitrate = "4.2 Mbps",
            FrameRate = "60 fps",
            AspectRatio = "16:9",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            Title = "Home United vs Away City",
            Buffer = "Buffer 100%",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var text = PlayerChromeText.FormatVideoInfo(model);

        // Assert — title, status, identity row (index · chip · name), height · bitrate, detail
        var lines = text.Split('\n');
        Assert.StartsWith("Home United vs Away City", text);
        Assert.Contains("Status: Playing", text);
        Assert.Contains("2/5 · V1 · Channel North", lines);
        Assert.Contains("1080p · 4.2 Mbps", lines);
        Assert.DoesNotContain("Channel North · 1080p", text);
        Assert.DoesNotContain("Stream: 2/5", text);
        Assert.DoesNotContain("Source: V1", text);
        Assert.DoesNotContain("Quality:", text);
        Assert.DoesNotContain("Resolution:", text);
        Assert.DoesNotContain("Bitrate:", text);
        Assert.DoesNotContain("Channel: Channel North", text);
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
        // Arrange — known identity stays; measured size/URLs go until the new stream settles
        var model = new VideoInfoPanelModel
        {
            PlaybackState = "Playing",
            StreamIndex = 3,
            StreamTotal = 5,
            Channel = "Channel North",
            SourceLabel = "V1",
            Quality = "540p",
            Resolution = "960x540",
            Bitrate = "1.8 Mbps",
            FrameRate = "30 fps",
            AspectRatio = "16:9",
            VideoCodec = "H.264",
            AudioCodec = "AAC",
            Title = "Home United vs Away City",
            Buffer = "80%",
            SourceUrl = "https://streams.example.com/live.m3u8",
            RefererHost = "referer.example.com"
        };

        // Act
        var text = PlayerChromeText.FormatVideoInfo(model, whileSwitching: true);

        // Assert — identity row keeps index, chip, and name; measured size stays off until settled
        Assert.Contains("Status: Switching", text);
        Assert.DoesNotContain("Status: Playing", text);
        Assert.Contains("3/5 · V1 · Channel North", text);
        Assert.DoesNotContain("Stream: 3/5", text);
        Assert.DoesNotContain("540p", text);
        Assert.DoesNotContain("Mbps", text);
        Assert.DoesNotContain("Source: V1", text);
        Assert.DoesNotContain("Channel: Channel North", text);
        Assert.Contains("Home United vs Away City", text);
        Assert.DoesNotContain("Aspect ratio:", text);
        Assert.DoesNotContain("Video:", text);
        Assert.DoesNotContain("https://streams.example.com", text);
        Assert.DoesNotContain("Referer:", text);
        Assert.DoesNotContain("Buffer ", text);
    }

    [Fact]
    public void FormatVideoInfoDetail_KeepsBufferLineWhenZeroPercent()
    {
        // Arrange — 0% must still occupy a row (Windows used to omit null/empty and reflow).
        var model = new VideoInfoPanelModel
        {
            Channel = "1X EN",
            AspectRatio = "16:9",
            Buffer = "Buffer 0%",
            VideoCodec = "H.264",
            AudioCodec = "AAC"
        };

        // Act
        var detail = PlayerChromeText.FormatVideoInfoDetail(model);

        // Assert
        Assert.Contains("Buffer 0%", detail);
        var lines = detail.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Aspect ratio: 16:9", lines[0]);
        Assert.Equal("Buffer 0%", lines[1]);
        Assert.DoesNotContain("Channel:", detail);
    }

    [Fact]
    public void FromPlayback_BufferZeroPercent_FormatsBufferLine()
    {
        // Arrange
        // Act
        var model = PlayerChromeText.FromPlayback(
            overlay: null,
            current: null,
            playbackState: "Playing",
            title: "Home vs Away",
            sourceUrl: null,
            refererUrl: null,
            bufferPercent: 0);

        // Assert
        Assert.Equal("Buffer 0%", model.Buffer);
    }

    [Fact]
    public void FormatVideoInfoDetail_WhileSwitching_IsEmpty()
    {
        // Arrange — channel now lives on the identity row, so detail stays blank while switching
        var model = new VideoInfoPanelModel
        {
            Channel = "Channel North",
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
        Assert.Equal(string.Empty, detail);
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
    [InlineData(2, 9, "V2", "Channel West", "1280x720", false, "2/9 · V2 · Channel West")]
    [InlineData(2, 9, null, "Channel West", "1280x720", false, "2/9 · Channel West")]
    [InlineData(2, 9, "V1", null, "1280x720", false, "2/9 · V1")]
    [InlineData(2, 9, "V2", "Channel West", null, false, "2/9 · V2 · Channel West")]
    [InlineData(2, 9, "V2", "Channel West", "1280x720", true, "2/9 · V2 · Channel West")]
    [InlineData(1, 0, "V2", "Channel West", "1280x720", false, "")]
    public void FormatStreamIdentityLine_JoinsIndexChipAndName_WithoutResolution(
        int index,
        int total,
        string? sourceLabel,
        string? channel,
        string? resolution,
        bool whileSwitching,
        string expected)
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            StreamIndex = index,
            StreamTotal = total,
            Channel = channel,
            Resolution = resolution,
            SourceLabel = sourceLabel
        };

        // Act
        var line = PlayerChromeText.FormatStreamIdentityLine(model, sourceLabel, whileSwitching);

        // Assert
        Assert.Equal(expected, line);
    }

    [Fact]
    public void EllipsizeStreamName_TrimsAndTruncatesLongNames()
    {
        // Arrange
        const string longName = "A very long stream name that exceeds thirty-two characters";

        // Act
        var shortened = PlayerChromeText.EllipsizeStreamName(longName);
        var blank = PlayerChromeText.EllipsizeStreamName("   ");
        var shortName = PlayerChromeText.EllipsizeStreamName("  Channel West  ");

        // Assert
        Assert.Equal(PlayerChromeText.StreamNameMaxLength, shortened!.Length);
        Assert.EndsWith("…", shortened);
        Assert.StartsWith("A very long stream name that e", shortened);
        Assert.Null(blank);
        Assert.Equal("Channel West", shortName);
    }

    [Fact]
    public void FormatStreamIdentityLine_EllipsizesLongChannelName()
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            StreamIndex = 1,
            StreamTotal = 3,
            Channel = "A very long stream name that exceeds thirty-two characters",
            Resolution = "1920x1080"
        };

        // Act
        var line = PlayerChromeText.FormatStreamIdentityLine(model, sourceLabel: "V2");

        // Assert
        Assert.StartsWith("1/3 · V2 · ", line);
        Assert.EndsWith("…", line);
        Assert.DoesNotContain("1080p", line);
        Assert.Contains("…", line);
        Assert.DoesNotContain("thirty-two characters", line);
    }

    [Theory]
    [InlineData("1280x720", "3.4 Mbps", "720p", "720p · 3.4 Mbps")]
    [InlineData("1920x1080", "5.0 Mbps", "1080p 60fps", "1080p · 5.0 Mbps")]
    [InlineData("1280x720", null, "720p", "720p")]
    [InlineData(null, "1.8 Mbps", "Unknown Quality", "1.8 Mbps")]
    [InlineData(null, null, "1080p 60fps 5000kbps", "1080p 60fps")]
    public void FormatQualityLine_PutsHeightAndBitrateSideBySide(
        string? resolution,
        string? bitrate,
        string? quality,
        string expected)
    {
        // Arrange
        var model = new VideoInfoPanelModel { Resolution = resolution, Bitrate = bitrate, Quality = quality };

        // Act
        var line = PlayerChromeText.FormatQualityLine(model);

        // Assert
        Assert.Equal(expected, line);
    }

    [Fact]
    public void FormatQualityLine_FollowsTheSettledHeight_WhenItChangesMidPlay()
    {
        // Arrange — the adapters publish the settled size into Resolution
        var before = new VideoInfoPanelModel { Resolution = "1280x720", Bitrate = "3.4 Mbps" };
        var after = new VideoInfoPanelModel { Resolution = "1920x1080", Bitrate = "3.4 Mbps" };

        // Act
        var first = PlayerChromeText.FormatQualityLine(before);
        var second = PlayerChromeText.FormatQualityLine(after);

        // Assert
        Assert.Equal("720p · 3.4 Mbps", first);
        Assert.Equal("1080p · 3.4 Mbps", second);
    }

    [Fact]
    public void FormatQualityLine_WhileSwitching_IsEmptyWithoutAProbe()
    {
        // Arrange
        var model = new VideoInfoPanelModel { Resolution = "1280x720", Bitrate = "3.4 Mbps", Quality = "720p" };

        // Act
        var line = PlayerChromeText.FormatQualityLine(model, whileSwitching: true);

        // Assert
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void FormatQualityLine_WhileSwitching_ShowsTargetProbeButNoBitrate()
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            Resolution = "960x540",
            ProbedResolution = "1920x1080",
            Bitrate = "1.8 Mbps"
        };

        // Act
        var line = PlayerChromeText.FormatQualityLine(model, whileSwitching: true);

        // Assert
        Assert.Equal("1080p", line);
    }

    [Fact]
    public void FormatStreamIdentitySuffix_NeverIncludesResolution()
    {
        // Arrange
        var without = new VideoInfoPanelModel { Channel = "Channel West", Resolution = null };
        var withRes = new VideoInfoPanelModel { Channel = "Channel West", Resolution = "1280x720", Bitrate = "3.4 Mbps" };

        // Act
        var a = PlayerChromeText.FormatStreamIdentitySuffix(without);
        var b = PlayerChromeText.FormatStreamIdentitySuffix(withRes);
        var c = PlayerChromeText.FormatStreamIdentitySuffix(withRes, whileSwitching: true);

        // Assert
        Assert.Equal("Channel West", a);
        Assert.Equal("Channel West", b);
        Assert.Equal("Channel West", c);
    }

    [Fact]
    public void FormatIdentityResolution_WhileSwitching_OmitsPreviousMeasuredSize()
    {
        // Arrange
        var model = new VideoInfoPanelModel { Resolution = "1280x720" };

        // Act
        var settled = PlayerChromeText.FormatIdentityResolution(model);
        var switching = PlayerChromeText.FormatIdentityResolution(model, whileSwitching: true);

        // Assert
        Assert.Equal("720p", settled);
        Assert.Null(switching);
    }

    [Fact]
    public void FormatIdentityResolution_WhileSwitching_ShowsTargetBitstreamProbe()
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            Resolution = "960x540",
            ProbedResolution = "1920x1080"
        };

        // Act
        var settled = PlayerChromeText.FormatIdentityResolution(model);
        var switching = PlayerChromeText.FormatIdentityResolution(model, whileSwitching: true);

        // Assert
        Assert.Equal("540p", settled);
        Assert.Equal("1080p", switching);
    }

    [Fact]
    public void FormatStreamIdentityLine_WhileSwitching_KeepsHeightOffTheNameRow()
    {
        // Arrange
        var model = new VideoInfoPanelModel
        {
            StreamIndex = 2,
            StreamTotal = 9,
            Channel = "Channel North",
            Resolution = "960x540",
            ProbedResolution = "1920x1080"
        };

        // Act
        var line = PlayerChromeText.FormatStreamIdentityLine(model, "V1", whileSwitching: true);

        // Assert
        Assert.Equal("2/9 · V1 · Channel North", line);
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
        Assert.DoesNotContain("Mbps", model.Quality, StringComparison.Ordinal);
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
