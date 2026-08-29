using System;
using VardyParty.HomeUi.Views;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Strip padding / row height follow TvFocusScrollMath. Selection chrome is
/// inset inside the card (FocusScale 1.0), so padding is comfort pad only.
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

        // Assert — comfort pad each side; symmetric.
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

        // Assert
        Assert.Equal(
            state.CardHeight + state.StripPaddingThickness.Top + state.StripPaddingThickness.Bottom,
            rowHeight);
    }

    [Fact]
    public void StripPadding_TvMetrics_CoversEdgeRingHalfStroke()
    {
        // Arrange
        var state = new HomeLayoutState();
        state.Apply(HomeLayoutClass.Tv);

        // Act
        var padding = state.StripPaddingThickness;

        // Assert — ceil(2.5 + 4) = 7 per side; RowHeight = 160 + 14 = 174.
        Assert.Equal(7, padding.Top);
        Assert.Equal(7, padding.Left);
        Assert.Equal(174, state.RowHeight);
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

        Assert.True(state.LeagueRowHeight < 400);
        Assert.True(state.LeagueRowHeight > state.RowHeight);
    }
}
