using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class LeagueDisplayNameTests
{
    [Theory]
    [InlineData("UEFA NATIONS LEAGUE", "UEFA Nations League")]
    [InlineData("CAF AFRICA CUP OF NATIONS QUALIFIERS", "CAF Africa Cup of Nations Qualifiers")]
    [InlineData("FIFA WORLD CUP", "FIFA World Cup")]
    [InlineData("MLS REGULAR SEASON", "MLS Regular Season")]
    [InlineData("NBA PLAYOFFS", "NBA Playoffs")]
    [InlineData("NFL REGULAR SEASON", "NFL Regular Season")]
    [InlineData("EFL CHAMPIONSHIP", "EFL Championship")]
    public void ForDisplay_TitleCasesAllCapsApiKeys_KeepingKnownAcronyms(string input, string expected)
    {
        Assert.Equal(expected, LeagueDisplayName.ForDisplay(input));
    }

    [Theory]
    [InlineData("UEFA Nations League")]
    [InlineData("CAF Africa Cup of Nations Qualifiers")]
    [InlineData("Premier League")]
    [InlineData("league alpha")]
    public void ForDisplay_LeavesMixedOrLowerCaseNamesAlone(string input)
    {
        Assert.Equal(input, LeagueDisplayName.ForDisplay(input));
    }

    [Fact]
    public void ForDisplay_TrimsWhitespace_AndTreatsBlankAsEmpty()
    {
        Assert.Equal("UEFA Nations League", LeagueDisplayName.ForDisplay("  UEFA NATIONS LEAGUE  "));
        Assert.Equal(string.Empty, LeagueDisplayName.ForDisplay("   "));
        Assert.Equal(string.Empty, LeagueDisplayName.ForDisplay(null));
    }

    [Fact]
    public void ForRow_PrefersBbcOrResolvedSpelling_OverAllCapsApiKey()
    {
        var apiOnly = new Game
        {
            League = "UEFA NATIONS LEAGUE",
            ApiLeague = "UEFA NATIONS LEAGUE",
            Start = DateTime.UtcNow.AddHours(1),
        };
        var matched = new Game
        {
            League = "UEFA NATIONS LEAGUE",
            ApiLeague = "UEFA NATIONS LEAGUE",
            BBCLeague = "UEFA Nations League",
            Start = DateTime.UtcNow.AddHours(2),
        };
        matched.ResolveLeague("UEFA Nations League");

        // The all-caps API game is first, which used to become group.Key.
        var heading = LeagueDisplayName.ForRow(new[] { apiOnly, matched });

        Assert.Equal("UEFA Nations League", heading);
        Assert.Equal("UEFA NATIONS LEAGUE", apiOnly.ApiLeague);
        Assert.Equal("UEFA NATIONS LEAGUE", matched.ApiLeague);
    }

    [Fact]
    public void ForRow_TitleCasesWhenTheGroupIsOnlyAnAllCapsApiKey()
    {
        var game = new Game
        {
            League = "UEFA NATIONS LEAGUE",
            ApiLeague = "UEFA NATIONS LEAGUE",
            Start = DateTime.UtcNow.AddHours(1),
        };

        Assert.Equal("UEFA Nations League", LeagueDisplayName.ForRow(new[] { game }));
        Assert.Equal("UEFA NATIONS LEAGUE", game.ApiLeague);
        Assert.Equal("UEFA NATIONS LEAGUE", game.League);
    }

    [Fact]
    public void ForRow_PrefersTitleCaseOverAllLowercaseWhenCasingDiffers()
    {
        var lower = new Game { League = "league alpha", Start = DateTime.UtcNow.AddHours(1) };
        var titled = new Game { League = "League Alpha", Start = DateTime.UtcNow.AddHours(2) };

        Assert.Equal("League Alpha", LeagueDisplayName.ForRow(new[] { lower, titled }));
    }

    [Fact]
    public void ForRow_EmptyOrBlankLeagues_FallsBackToOther()
    {
        Assert.Equal(HomeRowsBuilder.FallbackLeague, LeagueDisplayName.ForRow(Array.Empty<Game>()));
        Assert.Equal(
            HomeRowsBuilder.FallbackLeague,
            LeagueDisplayName.ForRow(new[] { new Game { League = "  ", Start = DateTime.UtcNow } }));
    }
}
