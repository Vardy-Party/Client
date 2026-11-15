using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class HomeRowsBuilderTests
{
    private static Game UpcomingGame(string league, DateTime startUtc) =>
        new() { League = league, Start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc) };

    private static Game LiveGame(string league) =>
        new() { League = league, IsInProgress = true, Minute = 30, Start = DateTime.UtcNow.AddHours(-1) };

    [Fact]
    public void Build_EmptyOrNull_ReturnsNoRows()
    {
        // Arrange
        // Act
        var fromNull = HomeRowsBuilder.Build(null);
        var fromEmpty = HomeRowsBuilder.Build(Array.Empty<Game>());

        // Assert
        Assert.Empty(fromNull);
        Assert.Empty(fromEmpty);
    }

    [Fact]
    public void Build_GroupsGamesByDisplayLeague()
    {
        // Arrange
        var games = new[]
        {
            UpcomingGame("League Alpha", new DateTime(2026, 8, 26, 14, 0, 0)),
            UpcomingGame("League Beta", new DateTime(2026, 8, 26, 15, 0, 0)),
            UpcomingGame("League Alpha", new DateTime(2026, 8, 26, 16, 0, 0)),
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(r => r.League == "League Alpha").Games.Count);
        Assert.Single(rows.Single(r => r.League == "League Beta").Games);
    }

    [Fact]
    public void Build_RowsWithLiveGamesComeFirst()
    {
        // Arrange: the upcoming-only league kicks off earlier than the live one.
        var games = new[]
        {
            UpcomingGame("Early League", DateTime.UtcNow.AddMinutes(10)),
            LiveGame("Live League"),
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        Assert.Equal("Live League", rows[0].League);
        Assert.True(rows[0].HasLiveGames);
        Assert.False(rows[1].HasLiveGames);
    }

    [Fact]
    public void Build_NonLiveRowsOrderedByEarliestKickoff()
    {
        // Arrange
        var games = new[]
        {
            UpcomingGame("Later League", new DateTime(2026, 8, 26, 20, 0, 0)),
            UpcomingGame("Sooner League", new DateTime(2026, 8, 26, 12, 0, 0)),
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        Assert.Equal(new[] { "Sooner League", "Later League" }, rows.Select(r => r.League));
    }

    [Fact]
    public void Build_PreservesMatchOrderInsideARow()
    {
        // Arrange: input arrives pre-ordered (live first), the builder must not resort it.
        var live = LiveGame("League Alpha");
        var upcoming = UpcomingGame("League Alpha", DateTime.UtcNow.AddHours(2));
        var games = new[] { live, upcoming };

        // Act
        var row = Assert.Single(HomeRowsBuilder.Build(games));

        // Assert
        Assert.Same(live, row.Games[0]);
        Assert.Same(upcoming, row.Games[1]);
    }

    [Fact]
    public void Build_BlankLeague_FallsBackToOther()
    {
        // Arrange
        var games = new[] { UpcomingGame("  ", new DateTime(2026, 8, 26, 12, 0, 0)) };

        // Act
        var row = Assert.Single(HomeRowsBuilder.Build(games));

        // Assert
        Assert.Equal(HomeRowsBuilder.FallbackLeague, row.League);
    }

    [Fact]
    public void Build_LeagueGroupingIsCaseInsensitive()
    {
        // Arrange
        var games = new[]
        {
            UpcomingGame("league alpha", new DateTime(2026, 8, 26, 12, 0, 0)),
            UpcomingGame("League Alpha", new DateTime(2026, 8, 26, 13, 0, 0)),
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Games.Count);
        Assert.Equal("League Alpha", row.League);
    }

    [Fact]
    public void Build_BbcLeagueNamePreferredForGrouping()
    {
        // Arrange
        var games = new[]
        {
            new Game { League = "api-name", BBCLeague = "Sample Premier Division", Start = DateTime.UtcNow.AddHours(1) },
            new Game { League = "Sample Premier Division", Start = DateTime.UtcNow.AddHours(2) },
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal("Sample Premier Division", row.League);
    }

    [Fact]
    public void Build_AllCapsApiLeague_IsTitleCasedForTheHeading_WithoutChangingApiLeague()
    {
        // Arrange
        var game = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            Start = new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc),
        };

        // Act
        var rows = HomeRowsBuilder.Build(new[] { game }, ["ZFA"]);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal("ZFA Northern Cup", row.League);
        Assert.Same(game, Assert.Single(row.Games));
        Assert.Equal("ZFA NORTHERN CUP", game.ApiLeague);
        Assert.Equal("ZFA NORTHERN CUP", game.League);
    }

    [Fact]
    public void Build_PrefersBbcSpellingWhenItDiffersOnlyByCaseFromTheApiKey()
    {
        // Arrange
        var unmatched = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            Start = new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc),
        };
        var matched = new Game
        {
            League = "ZFA NORTHERN CUP",
            ApiLeague = "ZFA NORTHERN CUP",
            BBCLeague = "ZFA Northern Cup",
            Start = new DateTime(2026, 11, 14, 19, 45, 0, DateTimeKind.Utc),
        };
        matched.ResolveLeague("ZFA Northern Cup");

        // Act
        var rows = HomeRowsBuilder.Build(new[] { unmatched, matched }, ["ZFA"]);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal("ZFA Northern Cup", row.League);
        Assert.Equal(2, row.Games.Count);
        Assert.Equal("ZFA NORTHERN CUP", unmatched.ApiLeague);
        Assert.Equal("ZFA NORTHERN CUP", matched.ApiLeague);
    }
}
