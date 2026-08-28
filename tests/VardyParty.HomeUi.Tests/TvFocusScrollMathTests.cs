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
    public void FocusChromeOverhead_TvMetrics_MatchesFieldEstimate()
    {
        // Arrange — TV card width 300dp, TV focus ring 5dp.
        const double cardWidth = 300;
        const double ringThickness = 5;

        // Act
        var overhead = TvFocusScrollMath.FocusChromeOverhead(cardWidth, ringThickness);

        // Assert — 13.5 scale overflow + 5.45 scaled ring + 4 comfort ≈ 23
        // per side, the "~24px" clipped-ring estimate from the field report.
        Assert.Equal(22.95, overhead, precision: 2);
    }

    [Fact]
    public void StripTarget_CardChromeAlreadyInsideViewport_NoScroll()
    {
        // Arrange — card at 400 with room for chrome on both sides.
        var target = default(double?);

        // Act
        target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft: 400, cardWidth: 300, overhead: 23,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 200);

        // Assert
        Assert.Null(target);
    }

    [Fact]
    public void StripTarget_CardLayoutVisibleButChromeClipped_ScrollsForChrome()
    {
        // Arrange — card layout rect ends exactly at the viewport's right
        // edge (the MakeVisible end state that still clipped the ring).
        const double cardLeft = 1100;
        const double cardWidth = 300;
        const double overhead = 23;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft, cardWidth, overhead,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 200);

        // Assert — right edge + chrome fits: 1100 + 300 + 23 - 1200 = 223.
        Assert.Equal(223, target);
    }

    [Fact]
    public void StripTarget_CardOffLeft_AlignsChromeToLeftEdge()
    {
        // Arrange
        const double cardLeft = 500;
        const double cardWidth = 300;
        const double overhead = 23;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft, cardWidth, overhead,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 900);

        // Assert — reveal left edge minus chrome: 500 - 23 = 477.
        Assert.Equal(477, target);
    }

    [Fact]
    public void StripTarget_FirstCard_ClampsToContentStart()
    {
        // Arrange — first card sits at the strip padding; chrome inflation
        // would ask for a negative scroll.
        const double cardLeft = 24;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft, cardWidth: 300, overhead: 23,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 600);

        // Assert
        Assert.Equal(0, target);
    }

    [Fact]
    public void StripTarget_LastCard_ClampsToMaxScroll()
    {
        // Arrange — last card at the content end; inflated right edge would
        // overshoot the scrollable range.
        const double contentWidth = 4000;
        const double viewportWidth = 1200;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft: 3676, cardWidth: 300, overhead: 23,
            viewportWidth: viewportWidth, contentWidth: contentWidth, currentScrollX: 2000);

        // Assert
        Assert.Equal(contentWidth - viewportWidth, target);
    }

    [Fact]
    public void StripTarget_CardPartiallyVisibleAtFarRightEdge_RevealsChromeFully()
    {
        // Arrange — viewport [200, 1400]; the card straddles the right edge
        // (visible 1300..1400 of 1300..1600): the framework's layout-rect
        // reveal parked exactly this state clipped.
        const double cardLeft = 1300;
        const double cardWidth = 300;
        const double overhead = 23;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft, cardWidth, overhead,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 200);

        // Assert — right edge + chrome inside: 1300 + 300 + 23 - 1200 = 423.
        Assert.Equal(423, target);
    }

    [Fact]
    public void StripTarget_CardPartiallyVisibleAtFarLeftEdge_RevealsChromeFully()
    {
        // Arrange — viewport [200, 1400]; the card straddles the left edge
        // (150..450, so 150..200 is clipped off-screen).
        const double cardLeft = 150;
        const double cardWidth = 300;
        const double overhead = 23;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft, cardWidth, overhead,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 200);

        // Assert — left edge minus chrome: 150 - 23 = 127.
        Assert.Equal(127, target);
    }

    [Fact]
    public void StripTarget_UnmeasuredGeometry_NoScroll()
    {
        // Arrange
        const double cardWidth = 0;

        // Act
        var target = TvFocusScrollMath.ComputeStripTarget(
            cardLeft: 100, cardWidth: cardWidth, overhead: 23,
            viewportWidth: 1200, contentWidth: 4000, currentScrollX: 0);

        // Assert
        Assert.Null(target);
    }

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
