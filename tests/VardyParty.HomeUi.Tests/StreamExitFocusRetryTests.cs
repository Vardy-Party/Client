using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class StreamExitFocusRetryTests
{
    [Fact]
    public void Choose_CardViewExists_PostsOnThatCard()
    {
        // Arrange
        const bool cardViewExists = true;

        // Act
        var wait = StreamExitFocusRetry.Choose(cardViewExists);

        // Assert
        Assert.Equal(StreamExitFocusRetry.Wait.PostOnCard, wait);
    }

    [Fact]
    public void Choose_NoCardYet_WaitsForTheNextLayoutPass()
    {
        // Arrange
        const bool cardViewExists = false;

        // Act
        var wait = StreamExitFocusRetry.Choose(cardViewExists);

        // Assert
        Assert.Equal(StreamExitFocusRetry.Wait.NextLayoutPass, wait);
    }

    [Fact]
    public void QuietHierarchy_FrameConsumesTheAttemptWithoutALayout()
    {
        // Arrange
        var attempt = new StreamExitFocusRetry.LayoutAttempt();

        // Act
        var frameContinued = attempt.TryContinue();
        var layoutContinued = attempt.TryContinue();

        // Assert
        Assert.True(frameContinued);
        Assert.False(layoutContinued);
    }

    [Fact]
    public void Cancel_DropsLayoutAndFrame()
    {
        // Arrange
        var attempt = new StreamExitFocusRetry.LayoutAttempt();

        // Act
        attempt.Cancel();
        var continued = attempt.TryContinue();

        // Assert
        Assert.False(continued);
    }
}
