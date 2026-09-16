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

    [Fact]
    public void FormatCountLabel_WhileIndeterminate_IsEmpty()
    {
        Assert.Equal(
            string.Empty,
            StreamResolveOverlayProgress.FormatCountLabel(
                streamsTested: 0,
                healthyStreams: 0,
                totalStreams: 0,
                indeterminate: true));
    }

    [Fact]
    public void FormatCountLabel_WhenTotalKnown_IncludesTotals()
    {
        Assert.Equal(
            "10 total • 3 tested • 1 healthy",
            StreamResolveOverlayProgress.FormatCountLabel(
                streamsTested: 3,
                healthyStreams: 1,
                totalStreams: 10,
                indeterminate: false));
    }

    [Fact]
    public void FormatCountLabel_WithoutTotal_ShowsTestedHealthyOnly()
    {
        Assert.Equal(
            "2 tested • 0 healthy",
            StreamResolveOverlayProgress.FormatCountLabel(
                streamsTested: 2,
                healthyStreams: 0,
                totalStreams: 0,
                indeterminate: false));
    }

    [Fact]
    public void FormatCountLabel_WhileIndeterminateWithTestedStreams_ShowsTestedHealthyOnly()
    {
        Assert.Equal(
            "2 tested • 0 healthy",
            StreamResolveOverlayProgress.FormatCountLabel(
                streamsTested: 2,
                healthyStreams: 0,
                totalStreams: 0,
                indeterminate: true));
    }

    [Theory]
    [InlineData("No working streams found", true)]
    [InlineData("No streams found", true)]
    [InlineData("No healthy streams found — try again or pick another game", true)]
    [InlineData("Local service unavailable. Ensure VardyParty Local Service is running on your LAN.", true)]
    [InlineData("Searching for streams...", false)]
    [InlineData(null, false)]
    public void IsExhaustedStatus_MatchesOrchestratorCopy(string? status, bool expected)
    {
        Assert.Equal(expected, StreamResolveOverlayProgress.IsExhaustedStatus(status));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Searching for streams", false)]
    [InlineData("Searching for streams...", false)]
    [InlineData("Finding streams", false)]
    [InlineData("Finding streams...", false)]
    [InlineData("Home United v Away City", true)]
    [InlineData("Testing stream 2 of 5", true)]
    public void ShouldShowStatusSubtitle_HidesTitleEcho(string? status, bool expected)
    {
        Assert.Equal(expected, StreamResolveOverlayProgress.ShouldShowStatusSubtitle(status));
    }
}