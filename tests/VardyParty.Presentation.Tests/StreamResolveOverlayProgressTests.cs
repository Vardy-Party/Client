using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class StreamResolveOverlayProgressTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 0.5)]
    [InlineData(10, 10, 1)]
    [InlineData(12, 10, 1)]
    public void Fraction_ClampsToUnitInterval(int tested, int total, double expected)
    {
        Assert.Equal(expected, StreamResolveOverlayProgress.Fraction(tested, total));
    }

    [Fact]
    public void IsIndeterminate_WhileTotalUnknown()
    {
        Assert.True(StreamResolveOverlayProgress.IsIndeterminate(0, noHealthyFound: false));
    }

    [Fact]
    public void IsIndeterminate_StopsOnceTotalIsKnown()
    {
        Assert.False(StreamResolveOverlayProgress.IsIndeterminate(4, noHealthyFound: false));
    }

    [Fact]
    public void IsIndeterminate_StopsOnNoHealthyDeadEnd()
    {
        Assert.False(StreamResolveOverlayProgress.IsIndeterminate(0, noHealthyFound: true));
    }

    [Theory]
    [InlineData("No working streams found", true)]
    [InlineData("No streams found", true)]
    [InlineData("No healthy streams found — try again or pick another game", true)]
    [InlineData("Searching for streams...", false)]
    [InlineData(null, false)]
    public void IsExhaustedStatus_MatchesOrchestratorCopy(string? status, bool expected)
    {
        Assert.Equal(expected, StreamResolveOverlayProgress.IsExhaustedStatus(status));
    }
}
