using System;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public sealed class MatchEventPresenterTests
{
    [Fact]
    public void Headline_Goal_ShowsScoreline()
    {
        var matchEvent = Event(MatchEventKind.Goal, homeScore: 2, awayScore: 1);

        Assert.Equal("GOAL — Jablonec 2–1 Rangers", MatchEventPresenter.Headline(matchEvent));
    }

    [Fact]
    public void Headline_ExtraTime_NamesTheFixture()
    {
        var matchEvent = Event(MatchEventKind.ExtraTime);

        Assert.Equal("EXTRA TIME — Jablonec v Rangers", MatchEventPresenter.Headline(matchEvent));
    }

    [Fact]
    public void Headline_Penalties_NamesTheFixture()
    {
        var matchEvent = Event(MatchEventKind.Penalties);

        Assert.Equal("PENALTIES — Jablonec v Rangers", MatchEventPresenter.Headline(matchEvent));
    }

    [Theory]
    [InlineData(MatchEventKind.Goal, "GOAL")]
    [InlineData(MatchEventKind.ExtraTime, "EXTRA TIME")]
    [InlineData(MatchEventKind.Penalties, "PENALTIES")]
    public void Kind_NamesTheEvent(MatchEventKind kind, string expected)
    {
        Assert.Equal(expected, MatchEventPresenter.Kind(Event(kind)));
    }

    [Fact]
    public void Fixture_Goal_StartsWithHomeTeamAndEndsWithAwayTeam()
    {
        // Banners put the home badge directly before this text and the away
        // badge directly after it, so nothing may precede the home team.
        var fixture = MatchEventPresenter.Fixture(Event(MatchEventKind.Goal, homeScore: 2, awayScore: 1));

        Assert.Equal("Jablonec 2–1 Rangers", fixture);
    }

    [Theory]
    [InlineData(MatchEventKind.ExtraTime)]
    [InlineData(MatchEventKind.Penalties)]
    public void Fixture_NonGoal_NamesTheTeamsOnly(MatchEventKind kind)
    {
        Assert.Equal("Jablonec v Rangers", MatchEventPresenter.Fixture(Event(kind)));
    }

    [Fact]
    public void LeagueName_UsesDisplayLeague()
    {
        Assert.Equal("Cup Alpha", MatchEventPresenter.LeagueName(Event(MatchEventKind.Goal)));
    }

    [Fact]
    public void LeagueName_BlankLeague_FallsBackLikeTheRowsBuilder()
    {
        var matchEvent = Event(MatchEventKind.Goal, league: "");

        Assert.Equal(HomeRowsBuilder.FallbackLeague, MatchEventPresenter.LeagueName(matchEvent));
    }

    private static MatchEvent Event(
        MatchEventKind kind,
        int homeScore = 1,
        int awayScore = 1,
        string league = "Cup Alpha")
    {
        var game = new Game
        {
            Home = "Jablonec",
            Away = "Rangers",
            League = league,
            Start = new DateTime(2026, 8, 27, 19, 0, 0, DateTimeKind.Utc),
            HomeScore = homeScore,
            AwayScore = awayScore,
            IsInProgress = true,
        };
        return new MatchEvent(kind, game, homeScore, awayScore);
    }
}
