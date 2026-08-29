using System;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MatchEventDetectorTests
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

    private static Game ExtraTimeGame(string home, string away, int homeScore, int awayScore)
    {
        var game = LiveGame(home, away, homeScore, awayScore);
        game.StatusText = "Extra time 98'";
        return game;
    }

    private static Game PenaltiesGame(string home, string away, int homeScore, int awayScore)
    {
        var game = LiveGame(home, away, homeScore, awayScore);
        game.StatusText = "Penalties";
        return game;
    }

    [Fact]
    public void Observe_FirstLoad_NeverRaises()
    {
        // Arrange: a live game already 2-1 when the app starts.
        var sut = new MatchEventDetector();

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 1) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_LiveScoreIncrease_RaisesGoalWithScoringContext()
    {
        // Arrange
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert
        var goal = Assert.Single(events);
        Assert.Equal(MatchEventKind.Goal, goal.Kind);
        Assert.Equal("Home United", goal.Game.Home);
        Assert.Equal(1, goal.HomeScore);
        Assert.Equal(0, goal.AwayScore);
        Assert.Equal(GoalSide.Home, goal.ScoringSide);
    }

    [Fact]
    public void Observe_AwayGoal_ReportsAwaySide()
    {
        // Arrange
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Assert
        var goal = Assert.Single(events);
        Assert.Equal(GoalSide.Away, goal.ScoringSide);
    }

    [Fact]
    public void Observe_BothScoresMoveInOneGap_ReportsBothSides()
    {
        // Arrange: a missed poll can carry two goals at once.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Assert
        var goal = Assert.Single(events);
        Assert.Equal(GoalSide.Both, goal.ScoringSide);
    }

    [Fact]
    public void Observe_UnchangedScore_DoesNotRaise()
    {
        // Arrange
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_GameAppearingMidUpdate_DoesNotRaise()
    {
        // Arrange: one game tracked, a second appears already 3-0.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        var events = sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 0, 0),
            LiveGame("Rovers", "Wanderers", 3, 0),
        });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_ScoreIncreaseOnFinishedGame_DoesNotRaise()
    {
        // Arrange: FT corrections must not fire the sting.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { FinishedGame("Home United", "Away City", 1, 0) });

        // Act
        var events = sut.Observe(new[] { FinishedGame("Home United", "Away City", 2, 0) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_ScoreCorrectionDownwards_DoesNotRaise()
    {
        // Arrange: VAR chalked one off.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 0) });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_MultipleGoalsAcrossGames_RaisesForEach()
    {
        // Arrange
        var sut = new MatchEventDetector();
        sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 0, 0),
            LiveGame("Rovers", "Wanderers", 1, 1),
        });

        // Act
        var events = sut.Observe(new[]
        {
            LiveGame("Home United", "Away City", 1, 0),
            LiveGame("Rovers", "Wanderers", 1, 2),
        });

        // Assert
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(MatchEventKind.Goal, e.Kind));
    }

    [Fact]
    public void Observe_NullUpdate_ReturnsEmpty()
    {
        // Arrange
        var sut = new MatchEventDetector();

        // Act
        var events = sut.Observe(null);

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Reset_ForgetsObservations_SoReappearanceIsSilent()
    {
        // Arrange: track, reset (sign-out), then the game returns with more goals.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 0, 0) });

        // Act
        sut.Reset();
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 2, 0) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_NullScoresTreatedAsZero_NoRaiseUntilRealGoal()
    {
        // Arrange: pre-enrichment games carry null scores.
        var sut = new MatchEventDetector();
        var pending = LiveGame("Home United", "Away City", 0, 0);
        pending.HomeScore = null;
        pending.AwayScore = null;
        sut.Observe(new[] { pending });

        // Act
        var events = sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 0) });

        // Assert: null -> 1 is a genuine transition.
        Assert.Single(events);
    }

    [Fact]
    public void Observe_TransitionIntoExtraTime_RaisesExtraTimeEvent()
    {
        // Arrange: the game was observed live at 90'.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Act
        var events = sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Assert
        var extraTime = Assert.Single(events);
        Assert.Equal(MatchEventKind.ExtraTime, extraTime.Kind);
        Assert.Null(extraTime.ScoringSide);
    }

    [Fact]
    public void Observe_FirstSightAlreadyInExtraTime_DoesNotRaise()
    {
        // Arrange & Act: a game appearing mid-extra-time must stay silent.
        var sut = new MatchEventDetector();
        var events = sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_StayingInExtraTime_RaisesOnlyOnce()
    {
        // Arrange
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });
        sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Act: still in extra time on the next poll.
        var events = sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void Observe_TransitionIntoPenalties_RaisesPenaltiesEvent()
    {
        // Arrange: extra time finished level.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });
        sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Act
        var events = sut.Observe(new[] { PenaltiesGame("Home United", "Away City", 1, 1) });

        // Assert
        var penalties = Assert.Single(events);
        Assert.Equal(MatchEventKind.Penalties, penalties.Kind);
    }

    [Fact]
    public void Observe_GoalDuringTransitionIntoExtraTime_RaisesBoth()
    {
        // Arrange: one poll gap carries a goal AND the phase transition.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });

        // Act
        var events = sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 2, 1) });

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Kind == MatchEventKind.Goal);
        Assert.Contains(events, e => e.Kind == MatchEventKind.ExtraTime);
    }

    [Fact]
    public void Observe_ExtraTimeGoal_RaisesGoalOnly()
    {
        // Arrange: already tracked in extra time.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { LiveGame("Home United", "Away City", 1, 1) });
        sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 1, 1) });

        // Act
        var events = sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 2, 1) });

        // Assert
        var goal = Assert.Single(events);
        Assert.Equal(MatchEventKind.Goal, goal.Kind);
    }

    [Fact]
    public void Observe_PenaltyShootoutScoreChanges_DoNotRaiseExtraEvents()
    {
        // Arrange: shoot-out "scores" arriving as score updates must not
        // machine-gun goal stings once the Penalties transition fired — the
        // BBC feed keeps the 90/120-minute score during the shoot-out, so an
        // unchanged score with the Penalties status stays silent.
        var sut = new MatchEventDetector();
        sut.Observe(new[] { ExtraTimeGame("Home United", "Away City", 2, 2) });
        sut.Observe(new[] { PenaltiesGame("Home United", "Away City", 2, 2) });

        // Act
        var events = sut.Observe(new[] { PenaltiesGame("Home United", "Away City", 2, 2) });

        // Assert
        Assert.Empty(events);
    }
}
