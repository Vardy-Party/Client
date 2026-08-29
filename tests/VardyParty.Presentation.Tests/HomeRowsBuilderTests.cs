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
        // Act & Assert
        Assert.Empty(HomeRowsBuilder.Build(null));
        Assert.Empty(HomeRowsBuilder.Build(Array.Empty<Game>()));
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
    }

    [Fact]
    public void Build_BbcLeagueNamePreferredForGrouping()
    {
        // Arrange
        var games = new[]
        {
            new Game { League = "api-name", BBCLeague = "Premier League", Start = DateTime.UtcNow.AddHours(1) },
            new Game { League = "Premier League", Start = DateTime.UtcNow.AddHours(2) },
        };

        // Act
        var rows = HomeRowsBuilder.Build(games);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal("Premier League", row.League);
    }
}
