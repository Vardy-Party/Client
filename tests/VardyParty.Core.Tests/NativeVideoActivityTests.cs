using System;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class NativeVideoActivityTests
    {
        [Fact]
        public void CanSwitchTo_RespectsPreparingAndSameUrl()
        {
            // Use SwitchingDecision helper instead of activity instance in unit tests
            Assert.True(SwitchingDecision.CanSwitch(null, "http://a", false));
            Assert.False(SwitchingDecision.CanSwitch(null, "http://b", true));
            Assert.False(SwitchingDecision.CanSwitch("http://a", "http://a", false));
        }
    }
}
