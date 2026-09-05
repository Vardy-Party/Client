using VardyParty.Kernel;
using VardyParty.TestSupport;
using Xunit;
using AutoFixture;

namespace VardyParty.Kernel.Tests
{
    public class OverlayFormattingTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        [Fact]
        public void PlayerOverlayInfo_HoldsValues()
        {
            // Arrange
            var info = _fixture.Build<PlayerOverlayInfo>()
                .With(i => i.Index, 2)
                .With(i => i.Total, 5)
                .With(i => i.Channel, "ChannelX")
                .With(i => i.BitrateKbps, 3000)
                .With(i => i.Resolution, "1080p")
                .With(i => i.Title, "Test")
                .Create();

            // Act
            var index = info.Index;
            var total = info.Total;
            var channel = info.Channel;
            var bitrate = info.BitrateKbps;
            var resolution = info.Resolution;
            var title = info.Title;

            // Assert
            Assert.Equal(2, index);
            Assert.Equal(5, total);
            Assert.Equal("ChannelX", channel);
            Assert.Equal(3000, bitrate);
            Assert.Equal("1080p", resolution);
            Assert.Equal("Test", title);
        }

        [Theory]
        [InlineData("1920x1080", "16:9")]
        [InlineData("1280X720", "16:9")]
        [InlineData("100x100", "1:1")]
        [InlineData("", null)]
        [InlineData("not-a-res", null)]
        public void BuildAspect_ParsesOrReturnsNull(string? input, string? expected)
        {
            // Arrange
            // Act
            var actual = PlayerOverlayFormatter.BuildAspect(input);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void BuildAspect_FromPixels_MatchesStringForm()
        {
            // Arrange
            // Act
            var fromPixels = PlayerOverlayFormatter.BuildAspect(1920u, 1080u);
            var fromString = PlayerOverlayFormatter.BuildAspect("1920x1080");

            // Assert
            Assert.Equal("16:9", fromPixels);
            Assert.Equal(fromString, fromPixels);
        }

        [Fact]
        public void StripQuery_RemovesQueryString()
        {
            // Arrange
            var url = "https://streams.example.com/live.m3u8?token=abc&exp=1";

            // Act
            var stripped = PlayerOverlayFormatter.StripQuery(url);

            // Assert
            Assert.Equal("https://streams.example.com/live.m3u8", stripped);
        }

        [Fact]
        public void RefererHost_ReturnsHostWhenAbsolute()
        {
            // Arrange
            var url = "https://catalog.example.com/match/1";

            // Act
            var host = PlayerOverlayFormatter.RefererHost(url);

            // Assert
            Assert.Equal("catalog.example.com", host);
        }

        [Theory]
        [InlineData("1920x1080", "1080p")]
        [InlineData("1280 X 720", "720p")]
        [InlineData("bad", null)]
        public void ExtractVerticalResolutionLabel_ParsesHeight(string? input, string? expected)
        {
            // Arrange
            // Act
            var actual = PlayerOverlayFormatter.ExtractVerticalResolutionLabel(input);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(1, 0, null, "Streams: 0")]
        [InlineData(2, 5, null, "Stream: 2/5")]
        [InlineData(2, 5, "720p", "Stream: 2/5 (720p)")]
        public void FormatStreamToast_BuildsExpectedCopy(int index, int total, string? vertical, string expected)
        {
            // Arrange
            // Act
            var actual = PlayerOverlayFormatter.FormatStreamToast(index, total, vertical);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void SourceBadgeStyle_ForFb_UsesBluePair()
        {
            // Arrange
            // Act
            var style = SourceBadgeStyle.ForLabel("FB");

            // Assert
            Assert.NotNull(style);
            Assert.Equal(0x1E, style!.Value.BgR);
            Assert.Equal(0x3A, style.Value.BgG);
            Assert.Equal(0x5F, style.Value.BgB);
            Assert.Equal("#1e3a5f", style.Value.BackgroundHex);
            Assert.Equal("#93c5fd", style.Value.ForegroundHex);
        }

        [Fact]
        public void SourceBadgeStyle_ForOther_UsesPurplePair()
        {
            // Arrange
            // Act
            var style = SourceBadgeStyle.ForLabel("V2");

            // Assert
            Assert.NotNull(style);
            Assert.Equal("#3b0764", style!.Value.BackgroundHex);
            Assert.Equal("#d8b4fe", style.Value.ForegroundHex);
        }

        [Fact]
        public void SourceBadgeStyle_ForEmpty_ReturnsNull()
        {
            // Arrange
            // Act
            var style = SourceBadgeStyle.ForLabel("  ");

            // Assert
            Assert.Null(style);
        }

        [Theory]
        [InlineData("avc1.640028", "H.264")]
        [InlineData("hvc1.1.6", "H.265")]
        [InlineData("vp9", "VP9")]
        [InlineData("mp4a.40.2", "AAC")]
        public void MapCodecToFriendlyName_MapsCommonTokens(string codec, string expected)
        {
            // Arrange
            // Act
            var actual = PlayerOverlayFormatter.MapCodecToFriendlyName(codec);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void BuildOverlayInfo_PrefersHealthResolutionAndMapsCodecs()
        {
            // Arrange
            var stream = _fixture.Build<Stream>()
                .With(s => s.Channel, "Channel North")
                .With(s => s.Resolution, "640x360")
                .With(s => s.BitrateKbps, 800)
                .Create();
            var health = _fixture.Build<StreamHealth>()
                .With(h => h.Resolution, "1920x1080")
                .With(h => h.FrameRate, 50)
                .With(h => h.VideoCodec, "avc1.4d401f")
                .With(h => h.AudioCodec, "mp4a.40.2")
                .With(h => h.Bitrate, 4500)
                .Create();
            var enriched = _fixture.Build<EnrichedStream>()
                .With(e => e.Stream, stream)
                .With(e => e.Health, health)
                .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/live.m3u8")
                .With(e => e.Referer, "https://referer.example.com/")
                .Create();

            // Act
            var info = PlayerOverlayFormatter.BuildOverlayInfo(
                enriched, index: 2, total: 4, refererUrl: enriched.Referer);

            // Assert
            Assert.NotNull(info);
            Assert.Equal(2, info!.Index);
            Assert.Equal(4, info.Total);
            Assert.Equal("Channel North", info.Channel);
            Assert.Equal("1920x1080", info.Resolution);
            Assert.Equal(50, info.FrameRate);
            Assert.Equal("H.264", info.VideoCodec);
            Assert.Equal("AAC", info.AudioCodec);
            Assert.Equal("16:9", info.AspectRatio);
            Assert.Equal("https://cdn.example.com/live.m3u8", info.M3u8Url);
            Assert.Equal("https://referer.example.com/", info.RefererUrl);
        }

        [Fact]
        public void BuildOverlayInfo_NullCurrentWithEmptyTotal_ReturnsNull()
        {
            // Arrange
            // Act
            var info = PlayerOverlayFormatter.BuildOverlayInfo(null, index: 0, total: 0);

            // Assert
            Assert.Null(info);
        }
    }
}
