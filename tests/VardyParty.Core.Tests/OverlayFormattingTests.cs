using System;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class OverlayFormattingTests
    {
        [Fact]
        public void PlayerOverlayInfo_HoldsValues()
        {
            var info = new PlayerOverlayInfo { Index = 2, Total = 5, Channel = "ChannelX", BitrateKbps = 3000, Resolution = "1080p", Title = "Test" };
            Assert.Equal(2, info.Index);
            Assert.Equal(5, info.Total);
            Assert.Equal("ChannelX", info.Channel);
            Assert.Equal(3000, info.BitrateKbps);
            Assert.Equal("1080p", info.Resolution);
            Assert.Equal("Test", info.Title);
        }
    }
}
