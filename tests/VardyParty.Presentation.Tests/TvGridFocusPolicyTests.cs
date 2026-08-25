using Xunit;
using VardyParty.Presentation;

namespace VardyParty.Presentation.Tests;

public class TvGridFocusPolicyTests
{
    [Fact]
    public void ShouldArmAutofocusOnCatalogRefresh_FirstGridAppearance_ArmsOnce()
    {
        // Arrange
        const bool gridAlreadyShown = false;
        const int focusedIndex = -1;
        const int displayedCount = 2;

        // Act
        var arm = TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
            gridAlreadyShown, focusedIndex, displayedCount);

        // Assert
        Assert.True(arm);
    }

    [Fact]
    public void ShouldArmAutofocusOnCatalogRefresh_LaterRefreshWithValidIndex_DoesNotStealDpad()
    {
        // Arrange
        const bool gridAlreadyShown = true;
        const int focusedIndex = 0;
        const int displayedCount = 2;

        // Act
        var arm = TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
            gridAlreadyShown, focusedIndex, displayedCount);

        // Assert
        Assert.False(arm);
    }

    [Fact]
    public void ShouldArmAutofocusOnCatalogRefresh_UnknownNativeFocus_DoesNotReArm()
    {
        // Arrange — D-pad may have moved without JS onfocus; do not yank to card 0.
        const bool gridAlreadyShown = true;
        const int focusedIndex = -1;
        const int displayedCount = 2;

        // Act
        var arm = TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
            gridAlreadyShown, focusedIndex, displayedCount);

        // Assert
        Assert.False(arm);
    }

    [Fact]
    public void ShouldArmAutofocusOnCatalogRefresh_ListShrunkPastFocusedCard_ArmsClampedRestore()
    {
        // Arrange
        const bool gridAlreadyShown = true;
        const int focusedIndex = 2;
        const int displayedCount = 2;

        // Act
        var arm = TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
            gridAlreadyShown, focusedIndex, displayedCount);
        var clamped = TvGridFocusPolicy.ClampFocusedIndex(focusedIndex, displayedCount);

        // Assert
        Assert.True(arm);
        Assert.Equal(1, clamped);
    }

    [Fact]
    public void ShouldArmAutofocusOnCatalogRefresh_EmptyList_DoesNotArm()
    {
        // Arrange
        const bool gridAlreadyShown = false;
        const int focusedIndex = 0;
        const int displayedCount = 0;

        // Act
        var arm = TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
            gridAlreadyShown, focusedIndex, displayedCount);

        // Assert
        Assert.False(arm);
    }

    [Fact]
    public void ShouldDeliverProgrammaticFocus_SecondAfterRender_DoesNotRefocus()
    {
        // Arrange
        const bool shouldFocus = true;
        const bool alreadyDelivered = true;

        // Act
        var deliver = TvGridFocusPolicy.ShouldDeliverProgrammaticFocus(shouldFocus, alreadyDelivered);

        // Assert
        Assert.False(deliver);
    }

    [Fact]
    public void ShouldDeliverProgrammaticFocus_RisingEdge_DeliversOnce()
    {
        // Arrange
        const bool shouldFocus = true;
        const bool alreadyDelivered = false;

        // Act
        var deliver = TvGridFocusPolicy.ShouldDeliverProgrammaticFocus(shouldFocus, alreadyDelivered);

        // Assert
        Assert.True(deliver);
    }

    [Fact]
    public void ShouldDeliverProgrammaticFocus_WhenShouldFocusFalse_DoesNotDeliver()
    {
        // Arrange
        const bool shouldFocus = false;
        const bool alreadyDelivered = false;

        // Act
        var deliver = TvGridFocusPolicy.ShouldDeliverProgrammaticFocus(shouldFocus, alreadyDelivered);

        // Assert
        Assert.False(deliver);
    }

    [Fact]
    public void ClampFocusedIndex_EmptyList_IsNegativeOne()
    {
        // Arrange
        const int focusedIndex = 3;
        const int displayedCount = 0;

        // Act
        var clamped = TvGridFocusPolicy.ClampFocusedIndex(focusedIndex, displayedCount);

        // Assert
        Assert.Equal(-1, clamped);
    }

    [Fact]
    public void ClampFocusedIndex_NegativeIndex_ClampsToFirstCard()
    {
        // Arrange
        const int focusedIndex = -1;
        const int displayedCount = 4;

        // Act
        var clamped = TvGridFocusPolicy.ClampFocusedIndex(focusedIndex, displayedCount);

        // Assert
        Assert.Equal(0, clamped);
    }

    [Fact]
    public void ClampFocusedIndex_InRange_Unchanged()
    {
        // Arrange
        const int focusedIndex = 2;
        const int displayedCount = 4;

        // Act
        var clamped = TvGridFocusPolicy.ClampFocusedIndex(focusedIndex, displayedCount);

        // Assert
        Assert.Equal(2, clamped);
    }
}
