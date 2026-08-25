using System;
using Xunit;
using VardyParty.Playback;

namespace VardyParty.Playback.Tests
{
    public class NativeVideoActivityTests
    {
        [Fact]
        public void CanSwitchTo_RespectsPreparingAndSameUrl()
        {
            // Arrange
            string? noCurrent = null;
            var urlA = "http://a";
            var urlB = "http://b";

            // Act
            var canWhenIdle = SwitchingDecision.CanSwitch(noCurrent, urlA, false);
            var cannotWhenPreparing = SwitchingDecision.CanSwitch(noCurrent, urlB, true);
            var cannotSameUrl = SwitchingDecision.CanSwitch(urlA, urlA, false);

            // Assert
            Assert.True(canWhenIdle);
            Assert.False(cannotWhenPreparing);
            Assert.False(cannotSameUrl);
        }
    }
}
