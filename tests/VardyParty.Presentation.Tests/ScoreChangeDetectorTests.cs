using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class ScoreChangeDetectorTests
{
    private static Game LiveGame(string home, string away, int homeScore, int awayScore) => new()
    {
        Home = home,
        Away = away,
        Start = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Utc),
        HomeScore = homeScore,
        AwayScore = awayScore,
        IsInProgress = true,
    };

    private static Game FinishedGame(string home, string away, int homeScore, int awayScore)
    {
        var game = LiveGame(home, away, homeScore, awayScore);
        game.IsInProgress = false;
        game.IsFinished = true;
        return game;
    }

    [Fact]
    public void Observe_FirstLoad_NeverRaises()
    {
        // Arrange: a live game already 2-1 when the app starts.
        var sut = new ScoreChangeDetector();

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 1) });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_LiveScoreIncrease_RaisesForThatGame()
    {
        // Arrange
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert
        var scorer = Assert.Single(scorers);
        Assert.Equal("Home United", scorer.Home);
    }

    [Fact]
    public void Observe_AwayGoal_AlsoRaises()
    {
        // Arrange
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Assert
        Assert.Single(scorers);
    }

    [Fact]
    public void Observe_UnchangedScore_DoesNotRaise()
    {
        // Arrange
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_GameAppearingMidUpdate_DoesNotRaise()
    {
        // Arrange: one game tracked, a second appears already 3-0.
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        var scorers = sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 0, 0),
            LiveGame("Rovers", "Wanderers", 3, 0),
        });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_ScoreIncreaseOnFinishedGame_DoesNotRaise()
    {
        // Arrange: FT corrections must not fire the sting.
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { FinishedGame("Home United", "Away City", 1, 0) });

        // Act
        var scorers = sut.Observe(new[] { FinishedGame("Home United", "Away City", 2, 0) });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_ScoreCorrectionDownwards_DoesNotRaise()
    {
        // Arrange: VAR chalked one off.
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 0) });

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_MultipleGoalsAcrossGames_RaisesForEach()
    {
        // Arrange
        var sut = new ScoreChangeDetector();
        sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 0, 0),
            LiveGame("Rovers", "Wanderers", 1, 1),
        });

        // Act
        var scorers = sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 1, 0),
            LiveGame("Rovers", "Wanderers", 1, 2),
        });

        // Assert
        Assert.Equal(2, scorers.Count);
    }

    [Fact]
    public void Observe_NullUpdate_ReturnsEmpty()
    {
        // Arrange
        var sut = new ScoreChangeDetector();

        // Act
        var scorers = sut.Observe(null);

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Reset_ForgetsObservations_SoReappearanceIsSilent()
    {
        // Arrange: track, reset (sign-out), then the game returns with more goals.
        var sut = new ScoreChangeDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        sut.Reset();
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 0) });

        // Assert
        Assert.Empty(scorers);
    }

    [Fact]
    public void Observe_NullScoresTreatedAsZero_NoRaiseUntilRealGoal()
    {
        // Arrange: pre-enrichment games carry null scores.
        var sut = new ScoreChangeDetector();
        var pending = LiveGame("Home United", "Away City", 0, 0);
        pending.HomeScore = null;
        pending.AwayScore = null;
        sut.Observe(new[] { pending });

        // Act
        var scorers = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert: null -> 1 is a genuine transition.
        Assert.Single(scorers);
    }
}
