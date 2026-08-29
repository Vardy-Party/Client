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
