using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class HomeLayoutTests
{
    [Fact]
    public void Classify_Television_AlwaysWins()
    {
        // Act & Assert: even a portrait-shaped surface is TV when the idiom says so.
        Assert.Equal(HomeLayoutClass.Tv, HomeLayoutClassifier.Classify(400, 800, isTelevision: true));
        Assert.Equal(HomeLayoutClass.Tv, HomeLayoutClassifier.Classify(1920, 1080, isTelevision: true));
    }

    [Fact]
    public void Classify_UnknownSize_DefaultsToDesktop()
    {
        // Act & Assert
        Assert.Equal(HomeLayoutClass.Desktop, HomeLayoutClassifier.Classify(0, 0, isTelevision: false));
        Assert.Equal(HomeLayoutClass.Desktop, HomeLayoutClassifier.Classify(-1, 500, isTelevision: false));
    }

    [Theory]
    [InlineData(1280, 800, HomeLayoutClass.Desktop)]
    [InlineData(700, 1200, HomeLayoutClass.Desktop)] // tablet portrait: shortest side above threshold
    [InlineData(844, 390, HomeLayoutClass.PhoneLandscape)]
    [InlineData(390, 844, HomeLayoutClass.PhonePortrait)]
    public void Classify_UsesShortestSideAndOrientation(double width, double height, HomeLayoutClass expected)
    {
        // Act & Assert
        Assert.Equal(expected, HomeLayoutClassifier.Classify(width, height, isTelevision: false));
    }

    [Fact]
    public void Metrics_TvIsLargestPhonePortraitIsSmallest()
    {
        // Act
        var tv = HomeLayoutMetrics.For(HomeLayoutClass.Tv);
        var desktop = HomeLayoutMetrics.For(HomeLayoutClass.Desktop);
        var landscape = HomeLayoutMetrics.For(HomeLayoutClass.PhoneLandscape);
        var portrait = HomeLayoutMetrics.For(HomeLayoutClass.PhonePortrait);

        // Assert: 10-foot UI scales monotonically down to phone portrait.
        Assert.True(tv.CardWidth > desktop.CardWidth);
        Assert.True(desktop.CardWidth > landscape.CardWidth);
        Assert.True(landscape.CardWidth > portrait.CardWidth);
        Assert.True(tv.ScoreFontSize > desktop.ScoreFontSize);
        Assert.True(tv.BadgeSize > portrait.BadgeSize);
    }

    [Fact]
    public void Metrics_TvCardsFitAGridOnA1080pPanel()
    {
        // Arrange: a 1080p TV panel; 24 is the focus-scale headroom the row
        // strip adds (HomeLayoutState.RowHeight), ~150 covers the page header.
        const double panelWidth = 1920;
        const double panelHeight = 1080;
        const double rowFocusHeadroom = 24;
        const double pageHeaderAllowance = 150;

        // Act
        var tv = HomeLayoutMetrics.For(HomeLayoutClass.Tv);

        // Assert: at least 4 cards per row and 3 league rows visible at once.
        var usableWidth = panelWidth - (2 * tv.PagePadding);
        Assert.True(usableWidth / (tv.CardWidth + tv.CardSpacing) >= 4);

        var usableHeight = panelHeight - (2 * tv.PagePadding) - pageHeaderAllowance;
        var rowCost = tv.CardHeight + rowFocusHeadroom + tv.RowSpacing;
        Assert.True(usableHeight / rowCost >= 3);
    }

    [Fact]
    public void Metrics_TvKeepsTenFootReadabilityFloors()
    {
        // Arrange
        var layoutClass = HomeLayoutClass.Tv;

        // Act
        var tv = HomeLayoutMetrics.For(layoutClass);

        // Assert: shrinking the cards must never shrink type below what reads
        // from the sofa — the status chip especially.
        Assert.True(tv.BadgeSize >= 52);
        Assert.True(tv.ScoreFontSize >= 32);
        Assert.True(tv.TeamFontSize >= 18);
        Assert.True(tv.StatusFontSize >= 14);
        Assert.True(tv.LeagueTitleFontSize >= 22);
    }

    [Fact]
    public void Metrics_EveryLayoutClassHasPositiveSizes()
    {
        foreach (HomeLayoutClass layoutClass in Enum.GetValues<HomeLayoutClass>())
        {
            // Act
            var metrics = HomeLayoutMetrics.For(layoutClass);

            // Assert
            Assert.True(metrics.CardWidth > 0);
            Assert.True(metrics.CardHeight > 0);
            Assert.True(metrics.BadgeSize > 0);
            Assert.True(metrics.TeamFontSize > 0);
            Assert.True(metrics.PagePadding > 0);
        }
    }
}
