using System;
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
    public void FocusChromeOverhead_IncludesHalfEdgeRingPlusComfortPad()
    {
        // Arrange — TV card 300×5; FocusScale 1.0, ring on the card edge.
        const double cardWidth = 300;
        const double ringThickness = 5;

        // Act
        var overhead = TvFocusScrollMath.FocusChromeOverhead(cardWidth, ringThickness);

        // Assert — half stroke (2.5) + comfort (4) = 6.5
        Assert.Equal(2.5 + TvFocusScrollMath.ChromeComfortPad, overhead);
        Assert.Equal(1.0, TvFocusScrollMath.FocusScale);
    }

    [Theory]
    [InlineData(300, 5)]  // TV card width, TV ring
    [InlineData(160, 5)]  // TV card height, TV ring
    [InlineData(350, 3)]  // Desktop card width
    [InlineData(192, 3)]  // Desktop card height
    [InlineData(272, 3)]  // Phone landscape card width
    [InlineData(140, 3)]  // Phone portrait card height
    public void FocusChromePadding_CoversOverheadWithinOneWholeDp(
        double cardDimension, double ringThickness)
    {
        // Arrange
        var overhead = TvFocusScrollMath.FocusChromeOverhead(cardDimension, ringThickness);

        // Act
        var padding = TvFocusScrollMath.FocusChromePadding(cardDimension, ringThickness);

        // Assert
        Assert.True(padding >= overhead);
        Assert.True(padding - overhead < 1);
        Assert.Equal(Math.Floor(padding), padding);
    }

    [Fact]
    public void FocusChromePadding_TvVerticalMetrics_CoversHalfRingAndComfort()
    {
        // Arrange — TV card height 160dp, 5dp edge ring.
        const double cardHeight = 160;
        const double ringThickness = 5;

        // Act
        var padding = TvFocusScrollMath.FocusChromePadding(cardHeight, ringThickness);

        // Assert — ceil(2.5 + 4) = 7
        Assert.Equal(7, padding);
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
    public void RowTopAlignDelta_RowAlreadyAtTop_NoScroll()
    {
        // Arrange
        const int rowTop = 0;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeRowTopAlignDelta(rowTop, viewport);

        // Assert
        Assert.Equal(0, delta);
    }

    [Fact]
    public void RowTopAlignDelta_RowLowerInViewport_ScrollsItToTheTop()
    {
        // Netflix-style: a fully visible row parked mid-viewport still
        // scrolls to the top on a vertical move — deterministic resting
        // position, rows above completely off-screen.
        const int rowTop = 420;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeRowTopAlignDelta(rowTop, viewport);

        // Assert
        Assert.Equal(420, delta);
    }

    [Fact]
    public void RowTopAlignDelta_RowAboveViewport_ScrollsUpToRowTop()
    {
        // Arrange
        const int rowTop = -180;
        const int viewport = 900;

        // Act
        var delta = TvFocusScrollMath.ComputeRowTopAlignDelta(rowTop, viewport);

        // Assert
        Assert.Equal(-180, delta);
    }

    [Fact]
    public void RowTopAlignDelta_UnmeasuredViewport_NoScroll()
    {
        // Arrange
        const int rowTop = 700;
        const int viewport = 0;

        // Act
        var delta = TvFocusScrollMath.ComputeRowTopAlignDelta(rowTop, viewport);

        // Assert
        Assert.Equal(0, delta);
    }

    [Fact]
    public void ShouldScrollRowIntoView_TvFirstRow_DoesNotScroll()
    {
        var first = new object();
        var items = new System.Collections.Generic.List<object> { first, new() };

        Assert.False(TvFocusScrollMath.ShouldScrollRowIntoView(isTv: true, items, first));
    }

    [Fact]
    public void ShouldScrollRowIntoView_TvLaterRow_Scrolls()
    {
        var first = new object();
        var second = new object();
        var items = new System.Collections.Generic.List<object> { first, second };

        Assert.True(TvFocusScrollMath.ShouldScrollRowIntoView(isTv: true, items, second));
    }

    [Fact]
    public void ShouldScrollRowIntoView_PointerClasses_AlwaysScroll()
    {
        var first = new object();
        var items = new System.Collections.Generic.List<object> { first };

        Assert.True(TvFocusScrollMath.ShouldScrollRowIntoView(isTv: false, items, first));
    }

    [Fact]
    public void ResolveStripContentWidth_PrefersReportedWhenWiderThanViewport()
    {
        var resolved = TvFocusScrollMath.ResolveStripContentWidth(
            reportedContentWidth: 1800, reportedInnerWidth: 400,
            viewportWidth: 800, summedChildExtent: 1200);

        Assert.Equal(1800, resolved);
    }

    [Fact]
    public void ResolveStripContentWidth_UsesChildSumWhenReportedIsViewport()
    {
        // Three 280dp cards + padding that MAUI reported as the 800dp viewport.
        var resolved = TvFocusScrollMath.ResolveStripContentWidth(
            reportedContentWidth: 800, reportedInnerWidth: 800,
            viewportWidth: 800, summedChildExtent: 1120);

        Assert.Equal(1120, resolved);
    }

    [Fact]
    public void SumHorizontalChildExtent_AddsTrailingPadding()
    {
        Assert.Equal(907, TvFocusScrollMath.SumHorizontalChildExtent(900, 7));
        Assert.Equal(0, TvFocusScrollMath.SumHorizontalChildExtent(0, 7));
    }

    [Theory]
    [InlineData(100, 2.75, 275)]
    [InlineData(100.4, 2, 201)]
    [InlineData(50, 0, 0)]
    public void DipToPx_RoundsDevicePixels(double dip, double density, int expected)
    {
        Assert.Equal(expected, TvFocusScrollMath.DipToPx(dip, density));
    }
}
