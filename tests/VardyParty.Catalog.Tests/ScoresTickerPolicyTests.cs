using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Catalog;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests;

public class ScoresTickerPolicyTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void Next_CyclesThroughAllModes()
    {
        // Arrange
        var mode = ScoresTickerMode.SameLeagueInPlay;

        // Act
        var allLeagues = ScoresTickerPolicy.Next(mode);
        var finished = ScoresTickerPolicy.Next(allLeagues);
        var upcoming = ScoresTickerPolicy.Next(finished);
        var back = ScoresTickerPolicy.Next(upcoming);

        // Assert
        Assert.Equal(ScoresTickerMode.AllLeaguesInPlay, allLeagues);
        Assert.Equal(ScoresTickerMode.AllFinished, finished);
        Assert.Equal(ScoresTickerMode.AllUpcoming, upcoming);
        Assert.Equal(ScoresTickerMode.SameLeagueInPlay, back);
    }

    [Fact]
    public void IsInPlay_ExcludesFinishedAndPostponed()
    {
        // Arrange
        var live = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.IsInProgress, true)
            .With(g => g.IsFinished, false)
            .With(g => g.StatusText, "45")
            .Create();
        var finished = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.IsFinished, true)
            .With(g => g.IsInProgress, false)
            .With(g => g.StatusText, "FT")
            .Create();

        // Act
        var liveResult = ScoresTickerPolicy.IsInPlay(live);
        var finishedResult = ScoresTickerPolicy.IsInPlay(finished);

        // Assert
        Assert.True(liveResult);
        Assert.False(finishedResult);
    }

    [Fact]
    public void IsSameLeague_TreatsBlankLeagueAsMatchAll()
    {
        // Arrange
        var game = _fixture.Build<Game>()
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "League Alpha")
            .Create();

        // Act
        var blank = ScoresTickerPolicy.IsSameLeague(game, null);
        var match = ScoresTickerPolicy.IsSameLeague(game, "League Alpha");
        var other = ScoresTickerPolicy.IsSameLeague(game, "League Beta");

        // Assert
        Assert.True(blank);
        Assert.True(match);
        Assert.False(other);
    }
}
