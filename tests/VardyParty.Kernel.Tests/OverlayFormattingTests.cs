using System;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.TestSupport;

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
    }
}
