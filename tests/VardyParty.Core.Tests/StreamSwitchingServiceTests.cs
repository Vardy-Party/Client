using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class StreamSwitchingServiceTests
    {
        private EnrichedStream MakeStream(string url, string channel, int? bitrate = null, string? resolution = null)
        {
            return new EnrichedStream
            {
                Stream = new Stream
                {
                    Url = url,
                    Channel = channel,
                    BitrateKbps = bitrate ?? 0,
                    Resolution = resolution
                },
                ResolvedM3U8Url = url,
                Status = StreamResolutionStatus.Healthy
            };
        }

        private EnrichedStream MakeStream(string sourceUrl, string refererUrl, string channel)
        {
            return new EnrichedStream
            {
                Stream = new Stream
                {
                    Url = refererUrl,
                    Channel = channel,
                    BitrateKbps = 0
                },
                Referer = refererUrl,
                ResolvedM3U8Url = sourceUrl,
                Status = StreamResolutionStatus.Healthy
            };
        }

        [Fact]
        public void Initialize_PublishesNullOverlay()
        {
            var svc = new StreamSwitchingService();

            PlayerOverlayInfo? lastOverlay = new PlayerOverlayInfo { Index = 1, Total = 1 };
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);

            // Call initialize - should emit null overlay
            svc.Initialize("L", "H", "A");

            Assert.Null(lastOverlay);
        }

        [Fact]
        public void Cleanup_PublishesNullOverlay()
        {
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);

            svc.Initialize("L","H","A");
            var s1 = MakeStream("http://a","ChannelA", 2500, "1080p");
            svc.AddHealthyStream(s1);

            Assert.NotNull(lastOverlay);

            svc.Cleanup();
            Assert.Null(lastOverlay);
        }

        [Fact]
        public void AddHealthyStream_PublishesOverlayInfo()
        {
            var svc = new StreamSwitchingService();

            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);

            svc.Initialize("L","H","A");
            var s = MakeStream("http://a","ChannelA", 2500, "1080p");
            svc.AddHealthyStream(s);

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
            var svc = new StreamSwitchingService();
            PlayerOverlayInfo? lastOverlay = null;
            svc.OverlayInfoChanged.Subscribe(info => lastOverlay = info);

            svc.Initialize("L","H","A");
            var s1 = MakeStream("http://a","ChannelA", 2500, "1080p");
            var s2 = MakeStream("http://b","ChannelB", 1500, "720p");
            svc.AddHealthyStream(s1);
            svc.AddHealthyStream(s2);

            // Switch to second stream (index 1)
            var switched = svc.SwitchToStream(1);
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

            svc.AddHealthyStream(first);
            svc.AddHealthyStream(duplicateM3u8);

            Assert.Single(svc.GetHealthyStreams());
            Assert.Equal("ChannelA", svc.GetHealthyStreams()[0].Stream.Channel);
        }

        [Fact]
        public void AddHealthyStream_DeduplicatesByResolvedM3u8_WhenOnlyRefererDiffers()
        {
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

            svc.AddHealthyStream(first);
            svc.AddHealthyStream(sameM3u8DifferentReferer);

            Assert.Single(svc.GetHealthyStreams());
            Assert.Equal("ChannelA", svc.GetHealthyStreams()[0].Stream.Channel);
        }

        [Fact]
        public void AddHealthyStream_DoesNotDeduplicate_WhenResolvedM3u8PathDiffers()
        {
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

            svc.AddHealthyStream(first);
            svc.AddHealthyStream(differentM3u8);

            Assert.Equal(2, svc.GetHealthyStreams().Count);
        }
    }
}
