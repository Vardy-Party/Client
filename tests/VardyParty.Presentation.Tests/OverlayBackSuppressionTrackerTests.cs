using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class OverlayBackSuppressionTrackerTests
{
    [Fact]
    public void IsSuppressed_WithNoVisibleOverlays_IsFalse()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();

        // Act
        var suppressed = tracker.IsSuppressed;

        // Assert
        Assert.False(suppressed);
        Assert.Equal("none", tracker.DescribeActive());
    }

    [Fact]
    public void Set_OverlayVisible_Suppresses()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();

        // Act
        tracker.Set("stream-resolve", visible: true);

        // Assert
        Assert.True(tracker.IsSuppressed);
        Assert.Equal("stream-resolve", tracker.DescribeActive());
    }

    [Fact]
    public void Set_OverlayHiddenAgain_StopsSuppressing()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();
        tracker.Set("device-code-sign-in", visible: true);

        // Act
        tracker.Set("device-code-sign-in", visible: false);

        // Assert
        Assert.False(tracker.IsSuppressed);
        Assert.Equal("none", tracker.DescribeActive());
    }

    [Fact]
    public void Set_OneOfTwoOverlaysHidden_StillSuppressesForTheOther()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();
        tracker.Set("stream-resolve", visible: true);
        tracker.Set("menu", visible: true);

        // Act
        tracker.Set("stream-resolve", visible: false);

        // Assert
        Assert.True(tracker.IsSuppressed);
        Assert.Equal("menu", tracker.DescribeActive());
    }

    [Fact]
    public void Set_SameOverlayReportedVisibleTwice_HiddenOnceClearsIt()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();
        tracker.Set("menu", visible: true);
        tracker.Set("menu", visible: true);

        // Act
        tracker.Set("menu", visible: false);

        // Assert
        Assert.False(tracker.IsSuppressed);
    }

    [Fact]
    public void Set_HidingAnOverlayThatWasNeverVisible_DoesNotThrowOrSuppress()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();

        // Act
        tracker.Set("menu", visible: false);

        // Assert
        Assert.False(tracker.IsSuppressed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Set_BlankOverlayName_IsIgnored(string? name)
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();

        // Act
        tracker.Set(name!, visible: true);

        // Assert
        Assert.False(tracker.IsSuppressed);
    }

    [Fact]
    public void Reset_ClearsAllOverlays_SoStaleStateNeverLeaksIntoANewSession()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();
        tracker.Set("stream-resolve", visible: true);
        tracker.Set("device-code-sign-in", visible: true);

        // Act
        tracker.Reset();

        // Assert
        Assert.False(tracker.IsSuppressed);
        Assert.Equal("none", tracker.DescribeActive());
    }

    [Fact]
    public void DescribeActive_ListsAllVisibleOverlaysInStableOrder()
    {
        // Arrange
        var tracker = new OverlayBackSuppressionTracker();
        tracker.Set("stream-resolve", visible: true);
        tracker.Set("menu", visible: true);

        // Act
        var description = tracker.DescribeActive();

        // Assert
        Assert.Equal("menu+stream-resolve", description);
    }
}
