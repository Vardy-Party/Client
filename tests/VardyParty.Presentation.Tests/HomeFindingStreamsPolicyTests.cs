using VardyParty.Kernel;
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
    public void PlaybackHideShouldCancelResolution_OnlyWhenViewerLeft()
    {
        Assert.True(HomeFindingStreamsPolicy.PlaybackHideShouldCancelResolution(null));
        Assert.True(HomeFindingStreamsPolicy.PlaybackHideShouldCancelResolution(
            PlaybackResult.Completed(PlaybackCloseReasons.UserClosed)));
        Assert.True(HomeFindingStreamsPolicy.PlaybackHideShouldCancelResolution(
            PlaybackResult.Completed(PlaybackCloseReasons.PlayerClosed)));
        Assert.False(HomeFindingStreamsPolicy.PlaybackHideShouldCancelResolution(
            PlaybackResult.Completed("Source error")));
    }

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

    [Fact]
    public void ShouldReleaseSupersededResolutionStart_OnlyWhenThisAttemptStillOwnsTheClaim()
    {
        Assert.True(HomeFindingStreamsPolicy.ShouldReleaseSupersededResolutionStart(
            attemptGeneration: 4,
            currentGeneration: 5,
            publishedClaimGeneration: 4));
        Assert.False(HomeFindingStreamsPolicy.ShouldReleaseSupersededResolutionStart(
            attemptGeneration: 4,
            currentGeneration: 5,
            publishedClaimGeneration: 5));
        Assert.False(HomeFindingStreamsPolicy.ShouldReleaseSupersededResolutionStart(
            attemptGeneration: 4,
            currentGeneration: 4,
            publishedClaimGeneration: 4));
    }

    [Fact]
    public void ShouldPaintFindingOverlay_OnlyForTheCurrentGeneration()
    {
        Assert.True(HomeFindingStreamsPolicy.ShouldPaintFindingOverlay(3, 3));
        Assert.False(HomeFindingStreamsPolicy.ShouldPaintFindingOverlay(3, 4));
    }
}
