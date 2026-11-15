using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class LeagueDisplayNameTests
{
    // Fictional acronyms, injected the way GamesApiSettings.LeagueAcronyms is.
    private static readonly string[] Acronyms = ["ZFA", "QRL", "XBL"];

    [Theory]
    [InlineData("ZFA NORTHERN CUP", "ZFA Northern Cup")]
    [InlineData("QRL CUP OF THE ISLES QUALIFIERS", "QRL Cup of the Isles Qualifiers")]
    [InlineData("XBL REGULAR SEASON", "XBL Regular Season")]
    [InlineData("NORTHERN CUP-ZFA STAGE", "Northern Cup-ZFA Stage")]
    public void ForDisplay_TitleCasesAllCapsApiKeys_KeepingConfiguredAcronyms(string input, string expected)
    {
        // Arrange
        // Act
        var result = LeagueDisplayName.ForDisplay(input, Acronyms);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ForDisplay_WordNotConfiguredAsAcronym_IsTitleCased()
    {
        // Arrange
        string[] withoutZfa = ["QRL"];

        // Act
        var result = LeagueDisplayName.ForDisplay("ZFA NORTHERN CUP", withoutZfa);

        // Assert
        Assert.Equal("Zfa Northern Cup", result);
    }

    [Fact]
    public void ForDisplay_NoConfiguredAcronyms_UsesTheDefaults()
    {
        // Arrange
        var defaults = GamesApiSettings.DefaultLeagueAcronyms;

        // Act
        var result = LeagueDisplayName.ForDisplay("NWSL SAMPLE SHIELD", Array.Empty<string>());

        // Assert
        Assert.Equal("NWSL Sample Shield", result);
        Assert.Contains("AFC", defaults);
        Assert.Contains("CONCACAF", defaults);
        Assert.Contains("CONMEBOL", defaults);
        Assert.Contains("USL", defaults);
        Assert.Contains("NWSL", defaults);
        Assert.Contains("WSL", defaults);
        Assert.Contains("SPL", defaults);
    }

    [Theory]
    [InlineData("ZFA Northern Cup")]
    [InlineData("QRL Cup of the Isles Qualifiers")]
    [InlineData("Sample Premier Division")]
    [InlineData("league alpha")]
    public void ForDisplay_LeavesMixedOrLowerCaseNamesAlone(string input)
    {
        // Arrange
        // Act
        var result = LeagueDisplayName.ForDisplay(input, Acronyms);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void ForDisplay_TrimsWhitespace_AndTreatsBlankAsEmpty()
    {
        // Arrange
        const string padded = "  ZFA NORTHERN CUP  ";

        // Act
        var trimmed = LeagueDisplayName.ForDisplay(padded, Acronyms);
        var blank = LeagueDisplayName.ForDisplay("   ", Acronyms);
        var missing = LeagueDisplayName.ForDisplay(null, Acronyms);

        // Assert
        Assert.Equal("ZFA Northern Cup", trimmed);
        Assert.Equal(string.Empty, blank);
        Assert.Equal(string.Empty, missing);
    }

    [Fact]
    public void ForRow_PrefersBbcOrResolvedSpelling_OverAllCapsApiKey()
    {
        // Arrange — the all-caps API game is first, which used to become group.Key.
        var apiOnly = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            Start = DateTime.UtcNow.AddHours(1),
        };
        var matched = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            BBCLeague = "ZFA Northern Cup",
            Start = DateTime.UtcNow.AddHours(2),
        };
        matched.ResolveLeague("ZFA Northern Cup");

        // Act
        var heading = LeagueDisplayName.ForRow(new[] { apiOnly, matched }, Acronyms);

        // Assert
        Assert.Equal("ZFA Northern Cup", heading);
        Assert.Equal("ZFA NORTHERN CUP", apiOnly.ApiLeague);
        Assert.Equal("ZFA NORTHERN CUP", matched.ApiLeague);
    }

    [Fact]
    public void ForRow_TitleCasesWhenTheGroupIsOnlyAnAllCapsApiKey()
    {
        // Arrange
        var game = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            Start = DateTime.UtcNow.AddHours(1),
        };

        // Act
        var heading = LeagueDisplayName.ForRow(new[] { game }, Acronyms);

        // Assert
        Assert.Equal("ZFA Northern Cup", heading);
        Assert.Equal("ZFA NORTHERN CUP", game.ApiLeague);
        Assert.Equal("ZFA NORTHERN CUP", game.League);
    }

    [Fact]
    public void ForRow_PrefersTitleCaseOverAllLowercaseWhenCasingDiffers()
    {
        // Arrange
        var lower = new Game { League = "league alpha", Start = DateTime.UtcNow.AddHours(1) };
        var titled = new Game { League = "League Alpha", Start = DateTime.UtcNow.AddHours(2) };

        // Act
        var heading = LeagueDisplayName.ForRow(new[] { lower, titled }, Acronyms);

        // Assert
        Assert.Equal("League Alpha", heading);
    }

    [Fact]
    public void ForRow_EmptyOrBlankLeagues_FallsBackToOther()
    {
        // Arrange
        var blank = new[] { new Game { League = "  ", Start = DateTime.UtcNow } };

        // Act
        var none = LeagueDisplayName.ForRow(Array.Empty<Game>(), Acronyms);
        var blankHeading = LeagueDisplayName.ForRow(blank, Acronyms);

        // Assert
        Assert.Equal(HomeRowsBuilder.FallbackLeague, none);
        Assert.Equal(HomeRowsBuilder.FallbackLeague, blankHeading);
    }
}
