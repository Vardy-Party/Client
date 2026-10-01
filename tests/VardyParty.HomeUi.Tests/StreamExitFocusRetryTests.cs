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
}
