using System;
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
        // Arrange — 180° is edge-on (the old mid-turn freeze): the
        // settle must ease forward to 360 and must not reverse the coin-flip.
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
    public void Pose_CoinFlip_YawsMarkAndDrivesEdgeSheen(double angle)
    {
        // Arrange — 90° is the edge-on mid-turn the view must lock.
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var absCos = Math.Abs(cos);
        var absSin = Math.Abs(Math.Sin(radians));

        // Act
        var pose = BrandCrestSpin.Pose(angle);

        // Assert — disc yaws; both faces use |cos|; front/back opacity by hemisphere.
        Assert.Equal(0, pose.MarkRotationDegrees);
        Assert.Equal(BrandCrestSpin.CameraTiltXDegrees, pose.FaceRotationX);
        Assert.Equal(angle, pose.FaceRotationY);
        Assert.Equal(absCos, pose.FaceScaleX, 5);
        Assert.True(pose.EdgeOpacity >= BrandCrestSpin.RimOpacityFloor - 0.001);
        var rimSin = Math.Max(absSin, 0.35);
        Assert.Equal(BrandCrestSpin.RimMinScaleX + (BrandCrestSpin.RimMaxScaleX - BrandCrestSpin.RimMinScaleX) * rimSin, pose.EdgeScaleX, 5);
        Assert.True(pose.DrivesSheen);
        if (cos >= 0)
        {
            Assert.Equal(0, pose.BackOpacity, 5);
            Assert.True(pose.FrontOpacity > 0);
        }
        else
        {
            Assert.Equal(0, pose.FrontOpacity, 5);
            Assert.True(pose.BackOpacity > 0);
        }

        if (absCos < 0.01)
        {
            Assert.True(pose.EdgeOpacity > 0.5);
        }
    }

    [Fact]
    public void Pose_FaceOnYaw_KeepsTiltAndRimDepth()
    {
        // Arrange / Act — yaw 0 used to blank the rim; tilt + floor keep depth.
        var pose = BrandCrestSpin.Pose(0);

        // Assert
        Assert.Equal(0, pose.FaceRotationY);
        Assert.Equal(BrandCrestSpin.CameraTiltXDegrees, pose.FaceRotationX);
        Assert.Equal(1, pose.FaceScaleX, 5);
        Assert.Equal(BrandCrestSpin.RimOpacityFloor, pose.EdgeOpacity, 5);
        Assert.Equal(1, pose.FrontOpacity, 5);
        Assert.Equal(0, pose.BackOpacity, 5);
    }

    [Fact]
    public void Pose_BackFaceOn_ShowsBackCrest()
    {
        // Arrange / Act — 180° is the reverse logo surface.
        var pose = BrandCrestSpin.Pose(180);

        // Assert
        Assert.Equal(180, pose.FaceRotationY);
        Assert.Equal(1, pose.FaceScaleX, 5);
        Assert.Equal(0, pose.FrontOpacity, 5);
        Assert.Equal(1, pose.BackOpacity, 5);
    }
}
