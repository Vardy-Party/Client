using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamMetricsWindowTests
{
    [Fact]
    public void Declines_AfterFourBufferingEvents()
    {
        // Arrange
        var afterOne = new StreamMetricsWindow();
        afterOne.AddBufferingEvent();
        var afterTwo = new StreamMetricsWindow();
        afterTwo.AddBufferingEvent();
        afterTwo.AddBufferingEvent();
        var afterThree = new StreamMetricsWindow();
        afterThree.AddBufferingEvent();
        afterThree.AddBufferingEvent();
        afterThree.AddBufferingEvent();
        var afterFour = new StreamMetricsWindow();
        afterFour.AddBufferingEvent();
        afterFour.AddBufferingEvent();
        afterFour.AddBufferingEvent();
        afterFour.AddBufferingEvent();

        // Act
        var declinedAfterOne = afterOne.IsHealthDeclined();
        var declinedAfterTwo = afterTwo.IsHealthDeclined();
        var declinedAfterThree = afterThree.IsHealthDeclined();
        var declinedAfterFour = afterFour.IsHealthDeclined();

        // Assert
        Assert.False(declinedAfterOne);
        Assert.False(declinedAfterTwo);
        Assert.False(declinedAfterThree);
        Assert.True(declinedAfterFour);
    }

    [Fact]
    public void Declines_WhenAverageBitrateBelow300()
    {
        // Arrange
        var w = new StreamMetricsWindow();
        w.AddBitrate(100);
        w.AddBitrate(200);
        w.AddBitrate(250);

        // Act
        var declined = w.IsHealthDeclined();

        // Assert
        Assert.True(declined);
    }

    [Fact]
    public void Declines_AfterThreeErrors()
    {
        // Arrange
        var afterTwo = new StreamMetricsWindow();
        afterTwo.AddError();
        afterTwo.AddError();
        var afterThree = new StreamMetricsWindow();
        afterThree.AddError();
        afterThree.AddError();
        afterThree.AddError();

        // Act
        var declinedAfterTwo = afterTwo.IsHealthDeclined();
        var declinedAfterThree = afterThree.IsHealthDeclined();

        // Assert
        Assert.False(declinedAfterTwo);
        Assert.True(declinedAfterThree);
    }

    [Fact]
    public void DoesNotDecline_WithFewerThanThreeBitrateSamples()
    {
        // Arrange
        var w = new StreamMetricsWindow();
        w.AddBitrate(50);
        w.AddBitrate(50);

        // Act
        var declined = w.IsHealthDeclined();

        // Assert
        Assert.False(declined);
    }

    [Fact]
    public void Declines_WhenTenSamplesAndLastThreeAllBelow500()
    {
        // Arrange
        var w = new StreamMetricsWindow();
        for (var i = 0; i < 7; i++)
            w.AddBitrate(2000);
        w.AddBitrate(400);
        w.AddBitrate(400);
        w.AddBitrate(400);

        // Act
        var declined = w.IsHealthDeclined();

        // Assert
        Assert.True(declined);
    }

    [Fact]
    public void DoesNotDecline_WhenLastThreeAreHealthyBitrate()
    {
        // Arrange
        var w = new StreamMetricsWindow();
        w.AddBitrate(2000);
        w.AddBitrate(1800);
        w.AddBitrate(1600);

        // Act
        var declined = w.IsHealthDeclined();

        // Assert
        Assert.False(declined);
    }
}
