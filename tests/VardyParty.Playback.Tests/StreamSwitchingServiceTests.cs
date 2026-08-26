using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Playback;
using VardyParty.TestSupport;

namespace VardyParty.Playback.Tests
{
    public class StreamSwitchingServiceTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        private EnrichedStream MakeStream(string url, string channel, int? bitrate = null, string? resolution = null)
        {
            var stream = _fixture.Build<Stream>()
                .With(s => s.Url, url)
                .With(s => s.Channel, channel)
                .With(s => s.BitrateKbps, bitrate ?? 0)
                .With(s => s.Resolution, resolution)
                .Create();

            return _fixture.Build<EnrichedStream>()
                .With(e => e.Stream, stream)
                .With(e => e.ResolvedM3U8Url, url)
                .With(e => e.Status, StreamResolutionStatus.Healthy)
                .Without(e => e.Health)
                .Create();
        }

        private EnrichedStream MakeStream(string sourceUrl, string refererUrl, string channel)
        {
            var stream = _fixture.Build<Stream>()
                .With(s => s.Url, refererUrl)
                .With(s => s.Channel, channel)
                .With(s => s.BitrateKbps, 0)
                .Create();

            return _fixture.Build<EnrichedStream>()
                .With(e => e.Stream, stream)
                .With(e => e.Referer, refererUrl)
                .With(e => e.ResolvedM3U8Url, sourceUrl)
                .With(e => e.Status, StreamResolutionStatus.Healthy)
                .Without(e => e.Health)
                .Create();
        }

        [Fact]
        public void Initialize_PublishesNullOverlay()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = _fixture.Build<PlayerOverlayInfo>()
                .With(i => i.Index, 1)
                .With(i => i.Total, 1)
                .Create();
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);

            // Act
            svc.Initialize("L", "H", "A");

            // Assert
            Assert.Null(lastOverlay);
        }

        [Fact]
        public void Cleanup_PublishesNullOverlay()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);
            svc.Initialize("L", "H", "A");
            var s1 = MakeStream("http://a", "ChannelA", 2500, "1080p");
            svc.AddHealthyStream(s1);
            var overlayAfterAdd = lastOverlay;

            // Act
            svc.Cleanup();

            // Assert
            Assert.NotNull(overlayAfterAdd);
            Assert.Null(lastOverlay);
        }

        [Fact]
        public void AddHealthyStream_PublishesOverlayInfo()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);
            svc.Initialize("L", "H", "A");
            var s = MakeStream("http://a", "ChannelA", 2500, "1080p");

            // Act
            svc.AddHealthyStream(s);

            // Assert
            Assert.NotNull(lastOverlay);
            Assert.Equal(1, lastOverlay!.Index);
            Assert.Equal(1, lastOverlay.Total);
            Assert.Equal("ChannelA", lastOverlay.Channel);
            Assert.Equal(2500, lastOverlay.BitrateKbps);
            Assert.Equal("1080p", lastOverlay.Resolution);
        }

        [Fact]
        public void SwitchToStream_UpdatesOverlayInfo()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);
            svc.Initialize("L", "H", "A");
            var s1 = MakeStream("http://a", "ChannelA", 2500, "1080p");
            var s2 = MakeStream("http://b", "ChannelB", 1500, "720p");
            svc.AddHealthyStream(s1);
            svc.AddHealthyStream(s2);

            // Act
            var switched = svc.SwitchToStream(1);

            // Assert
            Assert.True(switched);
            Assert.NotNull(lastOverlay);
            Assert.Equal(2, lastOverlay!.Index);
            Assert.Equal(2, lastOverlay.Total);
            Assert.Equal("ChannelB", lastOverlay.Channel);
            Assert.Equal(1500, lastOverlay.BitrateKbps);
            Assert.Equal("720p", lastOverlay.Resolution);
        }

        [Fact]
        public void AddHealthyStream_DeduplicatesByResolvedM3u8_WhenOnlyTokensDiffer()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            var first = MakeStream(
                "https://cdn.example.com/live/master.m3u8?token=abc123",
                "https://source.example.com/watch/match?auth=111",
                "ChannelA");
            var duplicateM3u8 = MakeStream(
                "https://CDN.example.com/live/master.m3u8?token=xyz789",
                "https://SOURCE.example.com/watch/match?auth=222",
                "ChannelB");

            // Act
            svc.AddHealthyStream(first);
            svc.AddHealthyStream(duplicateM3u8);

            // Assert
            Assert.Single(svc.GetHealthyStreams());
            Assert.Equal("ChannelA", svc.GetHealthyStreams()[0].Stream.Channel);
        }

        [Fact]
        public void AddHealthyStream_DeduplicatesByResolvedM3u8_WhenOnlyRefererDiffers()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            var first = MakeStream(
                "https://cdn.example.com/live/master.m3u8?token=abc123",
                "https://source.example.com/watch/match-one?auth=111",
                "ChannelA");
            var sameM3u8DifferentReferer = MakeStream(
                "https://cdn.example.com/live/master.m3u8?token=xyz789",
                "https://source.example.com/watch/match-two?auth=222",
                "ChannelB");

            // Act
            svc.AddHealthyStream(first);
            svc.AddHealthyStream(sameM3u8DifferentReferer);

            // Assert
            Assert.Single(svc.GetHealthyStreams());
            Assert.Equal("ChannelA", svc.GetHealthyStreams()[0].Stream.Channel);
        }

        [Fact]
        public void AddHealthyStream_DoesNotDeduplicate_WhenResolvedM3u8PathDiffers()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            var first = MakeStream(
                "https://cdn.example.com/live/one/master.m3u8?token=abc123",
                "https://source.example.com/watch/match?auth=111",
                "ChannelA");
            var differentM3u8 = MakeStream(
                "https://cdn.example.com/live/two/master.m3u8?token=xyz789",
                "https://source.example.com/watch/match?auth=222",
                "ChannelB");

            // Act
            svc.AddHealthyStream(first);
            svc.AddHealthyStream(differentM3u8);

            // Assert
            Assert.Equal(2, svc.GetHealthyStreams().Count);
        }

        [Fact]
        public void RemoveCurrentStream_LandsOnNextAtSameIndex_DoesNotSkip()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            svc.AddHealthyStream(MakeStream("http://a", "A"));
            svc.AddHealthyStream(MakeStream("http://b", "B"));
            svc.AddHealthyStream(MakeStream("http://c", "C"));
            var channelBeforeRemove = svc.GetCurrentStream()!.Stream.Channel;

            // Act
            var removed = svc.RemoveCurrentStream();

            // Assert
            Assert.Equal("A", channelBeforeRemove);
            Assert.True(removed);
            // Index stays at 0, which is now B — hosts must attach current, not SwitchToNext (that would skip to C).
            Assert.Equal("B", svc.GetCurrentStream()!.Stream.Channel);
            Assert.Equal(2, svc.GetHealthyStreams().Count);
            Assert.Equal(1, svc.GetCurrentStreamIndex());
        }

        [Fact]
        public void RemoveCurrentStream_LastItem_ClampsToNewLast()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            svc.AddHealthyStream(MakeStream("http://a", "A"));
            svc.AddHealthyStream(MakeStream("http://b", "B"));
            svc.SwitchToStream(1);

            // Act
            var removed = svc.RemoveCurrentStream();

            // Assert
            Assert.True(removed);
            Assert.Equal("A", svc.GetCurrentStream()!.Stream.Channel);
            Assert.Single(svc.GetHealthyStreams());
        }

        [Fact]
        public void RemoveCurrentStream_SoleStream_EmptiesPool()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            svc.AddHealthyStream(MakeStream("http://a", "A"));

            // Act
            var removed = svc.RemoveCurrentStream();

            // Assert
            Assert.True(removed);
            Assert.Null(svc.GetCurrentStream());
            Assert.Empty(svc.GetHealthyStreams());
        }

        [Fact]
        public void SwitchToNext_WrapsAround()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            svc.AddHealthyStream(MakeStream("http://a", "A"));
            svc.AddHealthyStream(MakeStream("http://b", "B"));
            svc.SwitchToStream(1);

            // Act
            var switched = svc.SwitchToNextStream();

            // Assert
            Assert.True(switched);
            Assert.Equal("A", svc.GetCurrentStream()!.Stream.Channel);
        }

        [Fact]
        public void GetNextHealthyStream_DoesNotChangeCurrent()
        {
            // Arrange
            var svc = new StreamSwitchingService();
            svc.Initialize("L", "H", "A");
            svc.AddHealthyStream(MakeStream("http://a", "A"));
            svc.AddHealthyStream(MakeStream("http://b", "B"));

            // Act
            var next = svc.GetNextHealthyStream();

            // Assert
            Assert.Equal("B", next!.Stream.Channel);
            Assert.Equal("A", svc.GetCurrentStream()!.Stream.Channel);
        }
    }
}
