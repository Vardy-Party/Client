using Xunit;
using VardyParty.HomeUi.Views;

namespace VardyParty.HomeUi.Tests;

public class BrandCrestSpinTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(179, 0)]
    [InlineData(179.9, 0)]
    [InlineData(180, 360)]
    [InlineData(180.1, 360)]
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
    public void RestTargetDegrees_ExactlyUpsideDown_EasesForwardNeverReversesTurn()
    {
        // Arrange — 180° is upside-down (the old mid-turn freeze): the
        // settle must ease forward to 360 and must not reverse the in-plane turn.
        const double current = 180;

        // Act
        var target = BrandCrestSpin.RestTargetDegrees(current);

        // Assert
        Assert.Equal(360, target);
    }

    [Fact]
    public void RestTargetDegrees_540_NormalizesThenRestsForwardAt360()
    {
        // Arrange — 540° normalizes to 180° (upside-down), which rests forward.
        const double current = 540;

        // Act
        var normalized = BrandCrestSpin.NormalizeDegrees(current);
        var target = BrandCrestSpin.RestTargetDegrees(current);

        // Assert
        Assert.Equal(180, normalized);
        Assert.Equal(360, target);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(359, true)]
    [InlineData(360, true)]
    public void IsFaceOnRest_DetectsUprightRest(double angle, bool expected)
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

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(359.5)]
    public void Pose_InPlaneTurn_RotatesMarkAndLeavesBezelScaleAndSheen(double angle)
    {
        // Arrange — 90° is the mid-turn case the view must lock.
        // Act
        var pose = BrandCrestSpin.Pose(angle);

        // Assert — the mark carries the angle. Bezel yaw stays 0, the face
        // is not squashed, and this frame does not drive the sheen.
        Assert.Equal(angle, pose.MarkRotationDegrees);
        Assert.Equal(0, pose.BezelRotationY);
        Assert.Equal(1, pose.FaceScaleX);
        Assert.Equal(0, pose.SheenOpacity);
        Assert.False(pose.DrivesSheen);
    }
}
