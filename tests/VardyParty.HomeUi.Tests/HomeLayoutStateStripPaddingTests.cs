using System;
using VardyParty.HomeUi.Views;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// The strip's layout padding and row height must be DERIVED from the focus
/// chrome the cards actually render (TvFocusScrollMath), never a separate
/// magic number: the field clipping came from a flat 12dp vertical headroom
/// that covered the scale overflow but not the ring on top of it.
/// </summary>
public class HomeLayoutStateStripPaddingTests
{
    [Theory]
    [InlineData(HomeLayoutClass.Tv)]
    [InlineData(HomeLayoutClass.Desktop)]
    [InlineData(HomeLayoutClass.PhoneLandscape)]
    [InlineData(HomeLayoutClass.PhonePortrait)]
    public void StripPadding_CoversFocusChromeOverhead_EveryLayoutClass(
        HomeLayoutClass layoutClass)
    {
        // Arrange
        var state = new HomeLayoutState();
        state.Apply(layoutClass);

        // Act
        var padding = state.StripPaddingThickness;

        // Assert — each side reserves at least the chrome overhead its own
        // axis renders (scale overflow scales with the dimension), and the
        // padding is symmetric so first/last cards and top/bottom edges get
        // identical room.
        var horizontalOverhead = TvFocusScrollMath.FocusChromeOverhead(
            state.CardWidth, state.FocusRingThickness);
        var verticalOverhead = TvFocusScrollMath.FocusChromeOverhead(
            state.CardHeight, state.FocusRingThickness);
        Assert.True(padding.Left >= horizontalOverhead);
        Assert.Equal(padding.Left, padding.Right);
        Assert.True(padding.Top >= verticalOverhead);
        Assert.Equal(padding.Top, padding.Bottom);
    }

    [Theory]
    [InlineData(HomeLayoutClass.Tv)]
    [InlineData(HomeLayoutClass.Desktop)]
    [InlineData(HomeLayoutClass.PhoneLandscape)]
    [InlineData(HomeLayoutClass.PhonePortrait)]
    public void RowHeight_WrapsCardPlusStripVerticalPadding_EveryLayoutClass(
        HomeLayoutClass layoutClass)
    {
        // Arrange
        var state = new HomeLayoutState();
        state.Apply(layoutClass);

        // Act
        var rowHeight = state.RowHeight;

        // Assert — the strip viewport is exactly the card plus its own
        // chrome padding: shorter would re-clip the ring inside the strip,
        // taller would waste vertical board space.
        Assert.Equal(
            state.CardHeight + state.StripPaddingThickness.Top + state.StripPaddingThickness.Bottom,
            rowHeight);
    }

    [Fact]
    public void StripPadding_TvMetrics_ReservesTheRingTheOldHeadroomClipped()
    {
        // Arrange
        var state = new HomeLayoutState();
        state.Apply(HomeLayoutClass.Tv);

        // Act
        var padding = state.StripPaddingThickness;

        // Assert — 17dp/side vertical (7.2 scale + 5.45 ring + 4 comfort,
        // ceiled) and 23dp/side horizontal at the TV metrics; RowHeight
        // follows to 194.
        Assert.Equal(17, padding.Top);
        Assert.Equal(23, padding.Left);
        Assert.Equal(194, state.RowHeight);
        // Header (max icon 40, title line) + Spacing 10 + RowHeight 194.
        Assert.Equal(
            Math.Max(state.LeagueIconSize, state.LeagueTitleFontSize * 1.4) + 10 + state.RowHeight,
            state.LeagueRowHeight);
    }

    [Theory]
    [InlineData(HomeLayoutClass.Tv)]
    [InlineData(HomeLayoutClass.Desktop)]
    [InlineData(HomeLayoutClass.PhoneLandscape)]
    [InlineData(HomeLayoutClass.PhonePortrait)]
    public void LeagueRowHeight_IsHeaderPlusStrip_NotViewportTall(HomeLayoutClass layoutClass)
    {
        var state = new HomeLayoutState();
        state.Apply(layoutClass);

        // Must be content-sized: far smaller than a 1080p leftover (~800+).
        // A viewport-tall item is what painted the black slab.
        Assert.True(state.LeagueRowHeight < 400);
        Assert.True(state.LeagueRowHeight > state.RowHeight);
    }
}
