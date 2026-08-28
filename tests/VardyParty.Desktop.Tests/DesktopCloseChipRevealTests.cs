using System;
using VardyParty.Desktop.Services;
using Xunit;

namespace VardyParty.Desktop.Tests;

public class DesktopCloseChipRevealTests
{
    [Fact]
    public void StartsHidden_WithThinReserve_NotABanner()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();

        // Act
        var height = sut.ReserveHeight(toastVisible: false);

        // Assert
        Assert.False(sut.ChipVisible);
        Assert.Equal(DesktopCloseChipReveal.HiddenReserveHeight, height);
        Assert.True(height < 40);
        Assert.True(DesktopCloseChipReveal.HitZoneWidth > 36);
    }

    [Fact]
    public void OnHoverEnter_RevealsAndCancelsAutoHide()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();

        // Act
        var action = sut.OnHoverEnter();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.True(sut.Hovering);
        Assert.Equal(DesktopCloseChipAction.CancelAutoHide, action);
        Assert.Equal(DesktopCloseChipReveal.RevealedReserveHeight, sut.ReserveHeight(toastVisible: false));
    }

    [Fact]
    public void OnHoverLeave_WhileRevealed_StartsAutoHideButKeepsChip()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnHoverLeave();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.False(sut.Hovering);
        Assert.Equal(DesktopCloseChipAction.StartAutoHide, action);
    }

    [Fact]
    public void OnAutoHideElapsed_HidesWhenNotHovering()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();
        sut.OnTouched();

        // Act
        var action = sut.OnAutoHideElapsed();

        // Assert
        Assert.False(sut.ChipVisible);
        Assert.Equal(DesktopCloseChipAction.None, action);
        Assert.Equal(DesktopCloseChipReveal.HiddenReserveHeight, sut.ReserveHeight(toastVisible: false));
    }

    [Fact]
    public void OnAutoHideElapsed_WhileHovering_StaysRevealed()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnAutoHideElapsed();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(DesktopCloseChipAction.CancelAutoHide, action);
    }

    [Fact]
    public void OnTouched_RevealsWithoutClosing_AndArmsIdleHide()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();

        // Act
        var action = sut.OnTouched();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(DesktopCloseChipAction.StartAutoHide, action);
    }

    [Fact]
    public void OnTouched_WhileHovering_DoesNotArmIdleHide()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();
        sut.OnHoverEnter();

        // Act
        var action = sut.OnTouched();

        // Assert
        Assert.True(sut.ChipVisible);
        Assert.Equal(DesktopCloseChipAction.CancelAutoHide, action);
    }

    [Fact]
    public void ReserveHeight_WhenToastVisible_IsAutoRegardlessOfChip()
    {
        // Arrange
        var sut = new DesktopCloseChipReveal();

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
        var sut = new DesktopCloseChipReveal();
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
        var nearHidden = DesktopCloseChipReveal.IsNearRestingPlace(
            width - 10, 8, width, revealed: false);
        var nearRevealed = DesktopCloseChipReveal.IsNearRestingPlace(
            width - 10, 30, width, revealed: true);
        var tooLowWhenHidden = DesktopCloseChipReveal.IsNearRestingPlace(
            width - 10, 30, width, revealed: false);
        var center = DesktopCloseChipReveal.IsNearRestingPlace(
            width / 2, 8, width, revealed: false);
        var offWindow = DesktopCloseChipReveal.IsNearRestingPlace(
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
        var delay = DesktopCloseChipReveal.AutoHideDelay;

        // Assert
        Assert.InRange(delay.TotalSeconds, 2, 3);
    }
}
