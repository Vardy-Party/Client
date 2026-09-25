using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MainActivityWindowFocusTests
{
    [Fact]
    public void ShouldRequestFocus_WhenWindowFocusedAndCurrentIsNullOrDecor()
    {
        // Arrange
        const bool hasWindowFocus = true;
        const bool currentFocusIsNullOrDecor = true;

        // Act
        var should = MainActivityWindowFocus.ShouldRequestFocus(
            hasWindowFocus, currentFocusIsNullOrDecor);

        // Assert
        Assert.True(should);
    }

    [Fact]
    public void ShouldRequestFocus_BeforeWindowFocus_DoesNotRequest()
    {
        // Arrange
        const bool hasWindowFocus = false;
        const bool currentFocusIsNullOrDecor = true;

        // Act
        var should = MainActivityWindowFocus.ShouldRequestFocus(
            hasWindowFocus, currentFocusIsNullOrDecor);

        // Assert
        Assert.False(should);
    }

    [Fact]
    public void ShouldRequestFocus_WhenContentAlreadyFocused_LeavesItAlone()
    {
        // Arrange
        const bool hasWindowFocus = true;
        const bool currentFocusIsNullOrDecor = false;

        // Act
        var should = MainActivityWindowFocus.ShouldRequestFocus(
            hasWindowFocus, currentFocusIsNullOrDecor);

        // Assert
        Assert.False(should);
    }
}
