using System;
using AutoFixture;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Catalog.Tests;

public class ScoresTickerTextTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void Build_SameLeague_ExcludesWatchedGameAndFinished()
    {
        // Arrange
        var watched = LiveGame("League Alpha", "Home United", "Away City", 2, 1, 70);
        var otherLive = LiveGame("League Alpha", "North Rovers", "South Wanderers", 0, 0, 12);
        var otherLeague = LiveGame("League Beta", "East Athletic", "West Town", 1, 0, 33);
        var finished = FinishedGame("League Alpha", "Old Park", "New Park", 3, 2);

        // Act
        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.SameLeagueInPlay,
            new[] { watched, otherLive, otherLeague, finished },
            "League Alpha",
            "Home United",
            "Away City");

        // Assert
        Assert.Equal("In-play: League Alpha", snapshot.Title);
        Assert.Contains("North Rovers", snapshot.FullText, StringComparison.Ordinal);
        Assert.DoesNotContain("Home United", snapshot.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("East Athletic", snapshot.FullText, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Park", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SameLeague_EmptyUsesOtherInPlayMessage()
    {
        // Arrange
        var watched = LiveGame("League Alpha", "Home United", "Away City", 1, 0, 10);

        // Act
        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.SameLeagueInPlay,
            new[] { watched },
            "League Alpha",
            "Home United",
            "Away City");

        // Assert
        Assert.Contains("No other in-play games in this league right now.", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AllLeaguesInPlay_PrefixesLeague()
    {
        // Arrange
        var live = LiveGame("League Alpha", "Home United", "Away City", 2, 1, 55);

        // Act
        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.AllLeaguesInPlay,
            new[] { live },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);

        // Assert
        Assert.Equal("All leagues in-play", snapshot.Title);
        Assert.Contains("[League Alpha]", snapshot.FullText, StringComparison.Ordinal);
        Assert.Contains("Home United", snapshot.FullText, StringComparison.Ordinal);
        Assert.Contains("2-1", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Finished_IncludesFtScores()
    {
        // Arrange
        var finished = FinishedGame("League Alpha", "Home United", "Away City", 4, 1);

        // Act
        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.AllFinished,
            new[] { finished },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);

        // Assert
        Assert.Equal("Finished games", snapshot.Title);
        Assert.Contains("4-1", snapshot.FullText, StringComparison.Ordinal);
        Assert.Contains("Home United", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Upcoming_UsesKickoffAndVs()
    {
        // Arrange
        var kickoff = new DateTime(2026, 9, 11, 15, 30, 0, DateTimeKind.Utc);
        var upcoming = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "League Alpha")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, (int?)null)
            .With(g => g.StatusText, "")
            .With(g => g.Start, kickoff)
            .Create();

        // Act
        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.AllUpcoming,
            new[] { upcoming },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);

        // Assert
        Assert.Equal("Upcoming games", snapshot.Title);
        Assert.Contains("vs", snapshot.FullText, StringComparison.Ordinal);
        Assert.Contains("Home United", snapshot.FullText, StringComparison.Ordinal);
        Assert.Contains("Away City", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Finished_ExcludesScorelessHeuristicFt()
    {
        var scored = FinishedGame("League Alpha", "Home United", "Away City", 2, 0);
        var heuristic = _fixture.Build<Game>()
            .With(g => g.Home, "North Rovers")
            .With(g => g.Away, "South Wanderers")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "League Alpha")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.IsFinished, true)
            .With(g => g.IsInProgress, false)
            .With(g => g.HomeScore, (int?)null)
            .With(g => g.AwayScore, (int?)null)
            .With(g => g.StatusText, "FT")
            .Create();

        var snapshot = ScoresTickerText.Build(
            ScoresTickerMode.AllFinished,
            new[] { scored, heuristic },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);

        Assert.Contains("2-0", snapshot.FullText, StringComparison.Ordinal);
        Assert.DoesNotContain("North Rovers", snapshot.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Upcoming_IncludesUnstartedWithoutLookAhead()
    {
        var farKickoff = DateTime.UtcNow.AddDays(5);
        var upcoming = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "League Alpha")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, (int?)null)
            .With(g => g.StatusText, "")
            .With(g => g.Start, farKickoff)
            .Create();

        var selected = ScoresTickerText.SelectGames(
            ScoresTickerMode.AllUpcoming,
            new[] { upcoming },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);

        Assert.Contains(upcoming, selected);
    }

    [Fact]
    public void BuildParts_Finished_IncludesScoreAndFtStatus()
    {
        var finished = FinishedGame("League Alpha", "Home United", "Away City", 4, 1);

        var parts = ScoresTickerText.BuildParts(
            ScoresTickerMode.AllFinished,
            new[] { finished },
            watchedLeague: null,
            watchedHome: null,
            watchedAway: null);
        var plain = InternationalTeamDisplay.PartsToPlainText(parts);

        Assert.Contains("Finished games:", plain, StringComparison.Ordinal);
        Assert.Contains("[League Alpha]", plain, StringComparison.Ordinal);
        Assert.Contains("4-1", plain, StringComparison.Ordinal);
        Assert.Contains("(FT)", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleFor_BlankLeague_UsesGenericInPlay()
    {
        // Arrange
        // Act
        var title = ScoresTickerText.TitleFor(ScoresTickerMode.SameLeagueInPlay, null);

        // Assert
        Assert.Equal("In-play games", title);
    }

    private Game LiveGame(string league, string home, string away, int homeScore, int awayScore, int minute)
    {
        return _fixture.Build<Game>()
            .With(g => g.Home, home)
            .With(g => g.Away, away)
            .With(g => g.League, league)
            .With(g => g.BBCLeague, league)
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.IsInProgress, true)
            .With(g => g.IsFinished, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, minute)
            .With(g => g.HomeScore, homeScore)
            .With(g => g.AwayScore, awayScore)
            .With(g => g.StatusText, $"{minute}'")
            .Create();
    }

    private Game FinishedGame(string league, string home, string away, int homeScore, int awayScore)
    {
        return _fixture.Build<Game>()
            .With(g => g.Home, home)
            .With(g => g.Away, away)
            .With(g => g.League, league)
            .With(g => g.BBCLeague, league)
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.IsFinished, true)
            .With(g => g.IsInProgress, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.HomeScore, homeScore)
            .With(g => g.AwayScore, awayScore)
            .With(g => g.StatusText, "FT")
            .Create();
    }
}
