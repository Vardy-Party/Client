using System;
using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxCloseChipRevealTests
{
    [Fact]
    public void StartsHidden_WithThinReserve_NotABanner()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();

        // Act
        var height = sut.ReserveHeight(toastVisible: false);

        // Assert
        Assert.False(sut.ChipVisible);
        Assert.Equal(LinuxCloseChipReveal.HiddenReserveHeight, height);
        Assert.True(height < 40);
        Assert.True(LinuxCloseChipReveal.HitZoneWidth > 36);
    }

    [Fact]
    public void OnHoverEnter_RevealsAndCancelsAutoHide()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();

        // Act
        var action = sut.OnHoverEnter();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.True(sut.Hovering);
        Assert.Equal(LinuxCloseChipAction.CancelAutoHide, action);
        Assert.Equal(LinuxCloseChipReveal.RevealedReserveHeight, sut.ReserveHeight(toastVisible: false));
    }

    [Fact]
    public void OnHoverLeave_WhileRevealed_StartsAutoHideButKeepsChip()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnHoverLeave();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.False(sut.Hovering);
        Assert.Equal(LinuxCloseChipAction.StartAutoHide, action);
    }

    [Fact]
    public void OnAutoHideElapsed_HidesWhenNotHovering()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();
        sut.OnTouched();

        // Act
        var action = sut.OnAutoHideElapsed();

        // Assert
        Assert.False(sut.ChipVisible);
        Assert.Equal(LinuxCloseChipAction.None, action);
        Assert.Equal(LinuxCloseChipReveal.HiddenReserveHeight, sut.ReserveHeight(toastVisible: false));
    }

    [Fact]
    public void OnAutoHideElapsed_WhileHovering_StaysRevealed()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnAutoHideElapsed();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(LinuxCloseChipAction.CancelAutoHide, action);
    }

    [Fact]
    public void OnTouched_RevealsWithoutClosing_AndArmsIdleHide()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();

        // Act
        var action = sut.OnTouched();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(LinuxCloseChipAction.StartAutoHide, action);
    }

    [Fact]
    public void OnTouched_WhileHovering_DoesNotArmIdleHide()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnTouched();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(LinuxCloseChipAction.CancelAutoHide, action);
    }

    [Fact]
    public void ReserveHeight_WhenToastVisible_IsAutoRegardlessOfChip()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();

        // Act
        var hidden = sut.ReserveHeight(toastVisible: true);
        sut.OnHoverEnter();
        var revealed = sut.ReserveHeight(toastVisible: true);

        // Assert
        Assert.True(double.IsNaN(hidden));
        Assert.True(double.IsNaN(revealed));
    }

    [Fact]
    public void Reset_HidesAndClearsHover()
    {
        // Arrange
        var sut = new LinuxCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        sut.Reset();

        // Assert
        Assert.False(sut.ChipVisible);
        Assert.False(sut.Hovering);
    }

    [Fact]
    public void IsNearRestingPlace_TopRightOnly()
    {
        // Arrange
        const double width = 1280;

        // Act
        var nearHidden = LinuxCloseChipReveal.IsNearRestingPlace(
            width - 10, 8, width, revealed: false);
        var nearRevealed = LinuxCloseChipReveal.IsNearRestingPlace(
            width - 10, 30, width, revealed: true);
        var tooLowWhenHidden = LinuxCloseChipReveal.IsNearRestingPlace(
            width - 10, 30, width, revealed: false);
        var center = LinuxCloseChipReveal.IsNearRestingPlace(
            width / 2, 8, width, revealed: false);
        var offWindow = LinuxCloseChipReveal.IsNearRestingPlace(
            -1, 0, width, revealed: false);

        // Assert
        Assert.True(nearHidden);
        Assert.True(nearRevealed);
        Assert.False(tooLowWhenHidden);
        Assert.False(center);
        Assert.False(offWindow);
    }

    [Fact]
    public void AutoHideDelay_IsTwoToThreeSeconds()
    {
        // Arrange
        // Act
        var delay = LinuxCloseChipReveal.AutoHideDelay;

        // Assert
        Assert.InRange(delay.TotalSeconds, 2, 3);
    }
}
