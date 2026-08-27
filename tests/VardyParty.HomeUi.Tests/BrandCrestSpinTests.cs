using Xunit;
using VardyParty.HomeUi.Views;

namespace VardyParty.HomeUi.Tests;

public class BrandCrestSpinTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(180, 0)]
    [InlineData(181, 360)]
    [InlineData(270, 360)]
    [InlineData(359, 360)]
    [InlineData(-20, 360)]
    public void RestTargetDegrees_TakesShortWayToFaceOn(double current, double expected)
    {
        // Arrange
        // Act
        var target = BrandCrestSpin.RestTargetDegrees(current);

        // Assert
        Assert.Equal(expected, target);
    }

    [Fact]
    public void RestTargetDegrees_540_NormalizesThenRestsAtZero()
    {
        // Arrange — 540° is face-on after one and a half turns (180°).
        const double current = 540;

        // Act
        var normalized = BrandCrestSpin.NormalizeDegrees(current);
        var target = BrandCrestSpin.RestTargetDegrees(current);

        // Assert
        Assert.Equal(180, normalized);
        Assert.Equal(0, target);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(359, true)]
    [InlineData(360, true)]
    public void IsFaceOnRest_DetectsCoinFace(double angle, bool expected)
    {
        // Arrange
        // Act
        var rest = BrandCrestSpin.IsFaceOnRest(angle);

        // Assert
        Assert.Equal(expected, rest);
    }

    [Fact]
    public void ContinueSpinCycle_FinishesTurnAfterCatalogArrives()
    {
        // Arrange
        const bool spinning = true;
        const bool settleRequested = true;

        // Act
        var repeat = BrandCrestSpin.ContinueSpinCycle(spinning, settleRequested);

        // Assert
        Assert.False(repeat);
    }

    [Fact]
    public void ContinueSpinCycle_KeepsTurningWhileLoading()
    {
        // Arrange
        const bool spinning = true;
        const bool settleRequested = false;

        // Act
        var repeat = BrandCrestSpin.ContinueSpinCycle(spinning, settleRequested);

        // Assert
        Assert.True(repeat);
    }

    [Fact]
    public void SettleNowBecauseSpinDied_WhenCatalogKilledAnimation()
    {
        // Arrange
        const bool settleRequested = true;
        const bool spinAnimationRunning = false;

        // Act
        var settleNow = BrandCrestSpin.SettleNowBecauseSpinDied(settleRequested, spinAnimationRunning);

        // Assert
        Assert.True(settleNow);
    }

    [Fact]
    public void SettleNowBecauseSpinDied_WaitsWhenTurnStillRunning()
    {
        // Arrange
        const bool settleRequested = true;
        const bool spinAnimationRunning = true;

        // Act
        var settleNow = BrandCrestSpin.SettleNowBecauseSpinDied(settleRequested, spinAnimationRunning);

        // Assert
        Assert.False(settleNow);
    }
}
