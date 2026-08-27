using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Pure-geometry coverage of the TV focus scroll ownership math. Values are
/// native px / dp coordinates, not tied to any real fixture data.
/// </summary>
public class TvFocusScrollMathTests
{
    [Fact]
    public void VerticalRevealDelta_RowFullyVisible_NoScroll()
    {
        // Arrange
        const int rowTop = 100;
        const int rowBottom = 400;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(rowTop, rowBottom, viewport);

        // Assert
        Assert.Equal(0, delta);
    }

    [Fact]
    public void VerticalRevealDelta_RowBelowViewport_ScrollsDownByOverflow()
    {
        // Arrange
        const int rowTop = 700;
        const int rowBottom = 1050;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(rowTop, rowBottom, viewport);

        // Assert
        Assert.Equal(150, delta);
    }

    [Fact]
    public void VerticalRevealDelta_RowAboveViewport_ScrollsUpToRowTop()
    {
        // Arrange
        const int rowTop = -180;
        const int rowBottom = 170;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(rowTop, rowBottom, viewport);

        // Assert
        Assert.Equal(-180, delta);
    }

    [Fact]
    public void VerticalRevealDelta_RowTallerThanViewport_AlignsRowTop()
    {
        // Arrange
        const int rowTop = 300;
        const int rowBottom = 1400;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(rowTop, rowBottom, viewport);

        // Assert
        Assert.Equal(300, delta);
    }

    [Fact]
    public void VerticalRevealDelta_UnmeasuredViewport_NoScroll()
    {
        // Arrange
        const int rowTop = 700;
        const int rowBottom = 1050;
        const int viewport = 0;

        // Act
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(rowTop, rowBottom, viewport);

        // Assert
        Assert.Equal(0, delta);
    }
}
