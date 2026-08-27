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
    public void ClassifyInitial_Television_WinsBeforeAnyDisplayInfo()
    {
        // Arrange: an Android TV host knows the Leanback flag at construction,
        // possibly before display info is available (zero density).

        // Act & Assert: the first paint must be Tv regardless of display data —
        // seeding after the first frame read as a startup "zoom" on real TVs.
        Assert.Equal(HomeLayoutClass.Tv, HomeLayoutClassifier.ClassifyInitial(true, 0, 0, 0));
        Assert.Equal(HomeLayoutClass.Tv, HomeLayoutClassifier.ClassifyInitial(true, 1920, 1080, 1));
    }

    [Fact]
    public void ClassifyInitial_UnknownDisplayInfo_FallsBackToDesktop()
    {
        // Arrange: headless/early hosts may have no usable display density.

        // Act & Assert: same fallback as Classify's unknown-size default.
        Assert.Equal(HomeLayoutClass.Desktop, HomeLayoutClassifier.ClassifyInitial(false, 1920, 1080, 0));
        Assert.Equal(HomeLayoutClass.Desktop, HomeLayoutClassifier.ClassifyInitial(false, 1920, 1080, -1));
    }

    [Theory]
    [InlineData(1920, 1080, 1.0, HomeLayoutClass.Desktop)] // desktop monitor
    [InlineData(1080, 2400, 3.0, HomeLayoutClass.PhonePortrait)] // phone, portrait pixels
    [InlineData(2400, 1080, 3.0, HomeLayoutClass.PhoneLandscape)] // phone, landscape pixels
    [InlineData(2560, 1600, 2.0, HomeLayoutClass.Desktop)] // tablet: shortest side above threshold
    public void ClassifyInitial_ConvertsPixelsToDipBeforeClassifying(
        double pixelWidth, double pixelHeight, double density, HomeLayoutClass expected)
    {
        // Arrange: physical display pixels + density, as DeviceDisplay reports.

        // Act
        var initialClass = HomeLayoutClassifier.ClassifyInitial(false, pixelWidth, pixelHeight, density);

        // Assert
        Assert.Equal(expected, initialClass);
    }

    [Fact]
    public void Metrics_TvTypeIsLargestPhonePortraitIsSmallest()
    {
        // Arrange
        var classes = new[]
        {
            HomeLayoutClass.Tv, HomeLayoutClass.Desktop,
            HomeLayoutClass.PhoneLandscape, HomeLayoutClass.PhonePortrait,
        };

        // Act
        var tv = HomeLayoutMetrics.For(classes[0]);
        var desktop = HomeLayoutMetrics.For(classes[1]);
        var landscape = HomeLayoutMetrics.For(classes[2]);
        var portrait = HomeLayoutMetrics.For(classes[3]);

        // Assert: after the third field-driven size notch the TV card BOX is
        // grid-sized for a 1080p panel and may sit below the desktop card
        // (score/badge included — the desktop window is 2 feet away, the TV
        // is 10 and won its density by user demand). Ten-foot readability is
        // guarded by Metrics_TvKeepsTenFootReadabilityFloors; here TV type
        // must stay above desktop body type and clearly above the phones.
        Assert.True(tv.TeamFontSize > desktop.TeamFontSize);
        Assert.True(tv.ScoreFontSize > landscape.ScoreFontSize);
        Assert.True(tv.BadgeSize > landscape.BadgeSize);
        Assert.True(tv.CardWidth >= landscape.CardWidth);
        Assert.True(desktop.CardWidth > landscape.CardWidth);
        Assert.True(landscape.CardWidth > portrait.CardWidth);
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

        // Assert: the third size notch targets ~5.5-6 cards per row and at
        // least 3.5 league rows visible at once.
        var usableWidth = panelWidth - (2 * tv.PagePadding);
        Assert.True(usableWidth / (tv.CardWidth + tv.CardSpacing) >= 5.5);

        var usableHeight = panelHeight - (2 * tv.PagePadding) - pageHeaderAllowance;
        var rowCost = tv.CardHeight + rowFocusHeadroom + tv.RowSpacing;
        Assert.True(usableHeight / rowCost >= 3.5);
    }

    [Fact]
    public void Metrics_TvKeepsTenFootReadabilityFloors()
    {
        // Arrange
        var layoutClass = HomeLayoutClass.Tv;

        // Act
        var tv = HomeLayoutMetrics.For(layoutClass);

        // Assert: shrinking the cards must never shrink type below what reads
        // from the sofa — the status chip especially. Floors revised with the
        // third size notch (300x160 cards): badge 50, score 30.
        Assert.True(tv.BadgeSize >= 50);
        Assert.True(tv.ScoreFontSize >= 30);
        Assert.True(tv.TeamFontSize >= 18);
        Assert.True(tv.StatusFontSize >= 14);
        Assert.True(tv.LeagueTitleFontSize >= 22);
    }

    [Fact]
    public void Metrics_PhoneNotchShowsAFullerStrip_AndKeepsReadabilityFloors()
    {
        // Arrange: typical phone viewports in dips (a 390x844 class device).
        const double landscapeWidth = 844;
        const double portraitWidth = 390;

        // Act
        var landscape = HomeLayoutMetrics.For(HomeLayoutClass.PhoneLandscape);
        var portrait = HomeLayoutMetrics.For(HomeLayoutClass.PhonePortrait);

        // Assert: the notch (272x150 / 244x140, down from 300x168 / 268x160)
        // fits noticeably more strip — landscape approaches 3 cards, portrait
        // a full card plus a real peek of the next.
        var landscapeUsable = landscapeWidth - (2 * landscape.PagePadding);
        Assert.True(landscapeUsable / (landscape.CardWidth + landscape.CardSpacing) >= 2.8);

        var portraitUsable = portraitWidth - (2 * portrait.PagePadding);
        Assert.True(portraitUsable / (portrait.CardWidth + portrait.CardSpacing) >= 1.4);

        // Readability floors at arm's length: type/badges scale down with the
        // cards but never below these.
        Assert.True(landscape.BadgeSize >= 42);
        Assert.True(landscape.ScoreFontSize >= 24);
        Assert.True(landscape.TeamFontSize >= 13);
        Assert.True(portrait.BadgeSize >= 38);
        Assert.True(portrait.ScoreFontSize >= 22);
        Assert.True(portrait.TeamFontSize >= 12);
        Assert.All(new[] { landscape, portrait }, m => Assert.True(m.StatusFontSize >= 12));
    }

    [Fact]
    public void Metrics_LeagueSectionsGetRealSeparation()
    {
        // Arrange: field report — league sections ran together, the next
        // header sat directly under the previous row's cards. RowSpacing is
        // the inter-league gap (applied above each header by the row template).
        var tv = HomeLayoutMetrics.For(HomeLayoutClass.Tv);
        var desktop = HomeLayoutMetrics.For(HomeLayoutClass.Desktop);
        var landscape = HomeLayoutMetrics.For(HomeLayoutClass.PhoneLandscape);
        var portrait = HomeLayoutMetrics.For(HomeLayoutClass.PhonePortrait);

        // Act
        var all = new[] { tv, desktop, landscape, portrait };

        // Assert: desktop gets a real gap; TV only modest breathing room (its
        // rows are deliberately tight so ~3.5 rows fit a 1080p panel — see
        // Metrics_TvCardsFitAGridOnA1080pPanel); every class keeps a floor.
        Assert.True(desktop.RowSpacing >= 36);
        Assert.True(tv.RowSpacing >= 30);
        Assert.All(all, m => Assert.True(m.RowSpacing >= 16));
    }

    [Fact]
    public void Metrics_LeagueIconReadsAsAProperMarkNextToTheTitle()
    {
        // Arrange: field report — the league icon rendered as a barely-legible
        // ~26px dot next to a 20px bold title on a desktop window.
        var tv = HomeLayoutMetrics.For(HomeLayoutClass.Tv);
        var desktop = HomeLayoutMetrics.For(HomeLayoutClass.Desktop);
        var landscape = HomeLayoutMetrics.For(HomeLayoutClass.PhoneLandscape);
        var portrait = HomeLayoutMetrics.For(HomeLayoutClass.PhonePortrait);

        // Act
        var all = new[] { tv, desktop, landscape, portrait };

        // Assert: the icon clears the title's line height on every class
        // (>= 1.6x the title font size), holds the 10-foot/desktop floors,
        // and still scales down through the classes.
        Assert.All(all, m => Assert.True(m.LeagueIconSize >= 1.6 * m.LeagueTitleFontSize));
        Assert.True(tv.LeagueIconSize >= 38);
        Assert.True(desktop.LeagueIconSize >= 32);
        Assert.True(tv.LeagueIconSize > desktop.LeagueIconSize);
        Assert.True(desktop.LeagueIconSize > landscape.LeagueIconSize);
        Assert.True(landscape.LeagueIconSize > portrait.LeagueIconSize);
    }

    [Fact]
    public void Metrics_TvFocusChromeIsUnmissableAtTenFeet()
    {
        // Arrange: field report — the 3px ring was effectively invisible from
        // the sofa. TV escalates ring thickness and adds a brightness lift of
        // the focused card; other classes keep the quiet chrome.
        var tv = HomeLayoutMetrics.For(HomeLayoutClass.Tv);
        var desktop = HomeLayoutMetrics.For(HomeLayoutClass.Desktop);

        // Assert
        Assert.True(tv.FocusRingThickness >= 4);
        Assert.InRange(tv.FocusedCardLift, 0.05, 0.2);
        Assert.Equal(3, desktop.FocusRingThickness);
        Assert.Equal(0, desktop.FocusedCardLift);
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
