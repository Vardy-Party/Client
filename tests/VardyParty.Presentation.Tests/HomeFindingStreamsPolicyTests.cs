using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class HomeFindingStreamsPolicyTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HardwareBackShouldCancelFinding_MatchesModalOwnership(bool owned, bool expected) =>
        Assert.Equal(expected, HomeFindingStreamsPolicy.HardwareBackShouldCancelFinding(owned));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlaybackLeaveShouldStopFinding_Always_EvenWhenOverlayAlreadyDismissed(bool findingActive) =>
        Assert.True(HomeFindingStreamsPolicy.PlaybackLeaveShouldStopFinding(findingActive));

    [Fact]
    public void IsFindingActive_WhenAnyOwnershipFlagSet()
    {
        Assert.True(HomeFindingStreamsPolicy.IsFindingActive(
            resolveOverlayOpen: true,
            isResolvingStreams: false,
            resolutionStartClaimed: false,
            resolutionTaskInFlight: false));
        Assert.True(HomeFindingStreamsPolicy.IsFindingActive(
            resolveOverlayOpen: false,
            isResolvingStreams: true,
            resolutionStartClaimed: false,
            resolutionTaskInFlight: false));
        Assert.True(HomeFindingStreamsPolicy.IsFindingActive(
            resolveOverlayOpen: false,
            isResolvingStreams: false,
            resolutionStartClaimed: true,
            resolutionTaskInFlight: false));
        Assert.True(HomeFindingStreamsPolicy.IsFindingActive(
            resolveOverlayOpen: false,
            isResolvingStreams: false,
            resolutionStartClaimed: false,
            resolutionTaskInFlight: true));
        Assert.False(HomeFindingStreamsPolicy.IsFindingActive(
            resolveOverlayOpen: false,
            isResolvingStreams: false,
            resolutionStartClaimed: false,
            resolutionTaskInFlight: false));
    }
}
