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
    }
}
