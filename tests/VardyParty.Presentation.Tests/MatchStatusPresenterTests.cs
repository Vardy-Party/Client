using System;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MatchStatusPresenterTests
{
    [Fact]
    public void GetPhase_Postponed_WinsOverEverything()
    {
        // Arrange
        var game = new Game { StatusText = "Postponed", IsInProgress = true };

        // Act & Assert
        Assert.Equal(MatchPhase.Postponed, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("Postponed", MatchStatusPresenter.GetStatusText(game));
    }

    [Fact]
    public void GetPhase_Finished_IsFullTime()
    {
        // Arrange
        var game = new Game { IsFinished = true };

        // Act & Assert
        Assert.Equal(MatchPhase.FullTime, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("FT", MatchStatusPresenter.GetStatusText(game));
    }

    [Fact]
    public void GetPhase_HalfTime_ReturnsHtChip()
    {
        // Arrange
        var game = new Game { IsHalfTime = true };

        // Act & Assert
        Assert.Equal(MatchPhase.HalfTime, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("HT", MatchStatusPresenter.GetStatusText(game));
    }

    [Theory]
    [InlineData("Penalties")]
    [InlineData("2-2 pens")]
    public void GetPhase_Penalties_ReturnsPensChip(string statusText)
    {
        // Arrange
        var game = new Game { StatusText = statusText, IsInProgress = true };

        // Act & Assert
        Assert.Equal(MatchPhase.Penalties, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("Pens", MatchStatusPresenter.GetStatusText(game));
    }

    [Fact]
    public void GetPhase_ExtraTime_ShowsStatusText()
    {
        // Arrange
        var game = new Game { StatusText = "Extra time 98'", IsInProgress = true };

        // Act & Assert
        Assert.Equal(MatchPhase.ExtraTime, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("Extra time 98'", MatchStatusPresenter.GetStatusText(game));
    }

    [Fact]
    public void GetStatusText_LiveWithStoppageTime_ShowsInjuryMinutes()
    {
        // Arrange: Minute encodes stoppage as base*100+extra (45+2 => 4502).
        var game = new Game { IsInProgress = true, Minute = 4502 };

        // Act & Assert
        Assert.Equal(MatchPhase.Live, MatchStatusPresenter.GetPhase(game));
        Assert.Equal("45+2'", MatchStatusPresenter.GetStatusText(game));
    }

    [Fact]
    public void GetStatusText_Upcoming_FormatsKickoffTime()
    {
        // Arrange: pin the culture so "tt"/"MMM" render the same on every runner.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var now = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Local);
        var today = new Game { Start = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Local) };
        var tomorrow = new Game { Start = new DateTime(2026, 8, 27, 12, 30, 0, DateTimeKind.Local) };
        var later = new Game { Start = new DateTime(2026, 9, 2, 20, 0, 0, DateTimeKind.Local) };

        // Act & Assert
        Assert.Equal("3:00 PM", MatchStatusPresenter.GetStatusText(today, now));
        Assert.Equal("Tomorrow 12:30 PM", MatchStatusPresenter.GetStatusText(tomorrow, now));
        Assert.Equal("Sep 02, 8:00 PM", MatchStatusPresenter.GetStatusText(later, now));
    }

    [Fact]
    public void FormatStartTime_UtcKind_ConvertsToLocalExactlyOnce()
    {
        // Arrange: expected value computed with the same single conversion the
        // presenter must perform, so the test is timezone-agnostic on any runner.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var startUtc = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
        var expectedLocal = startUtc.ToLocalTime();
        var nowLocal = expectedLocal.Date.AddHours(9);

        // Act
        var text = MatchStatusPresenter.FormatStartTime(startUtc, nowLocal);

        // Assert
        Assert.Equal(expectedLocal.ToString("h:mm tt"), text);
    }

    [Fact]
    public void FormatStartTime_LocalKind_IsNotConvertedAgain()
    {
        // Arrange: an already-local value must render its own wall clock.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var startLocal = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Local);
        var nowLocal = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Local);

        // Act
        var text = MatchStatusPresenter.FormatStartTime(startLocal, nowLocal);

        // Assert
        Assert.Equal("3:00 PM", text);
    }

    [Fact]
    public void FormatStartTime_UnspecifiedKind_IsTreatedAsUtc()
    {
        // Arrange: ingestion normalizes to UTC, so any leaked Unspecified value
        // must be read as UTC — identical output to the same ticks with Kind.Utc.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var startUnspecified = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Unspecified);
        var startUtc = DateTime.SpecifyKind(startUnspecified, DateTimeKind.Utc);
        var nowLocal = startUtc.ToLocalTime().Date.AddHours(9);

        // Act
        var unspecifiedText = MatchStatusPresenter.FormatStartTime(startUnspecified, nowLocal);
        var utcText = MatchStatusPresenter.FormatStartTime(startUtc, nowLocal);

        // Assert
        Assert.Equal(utcText, unspecifiedText);
    }

    [Fact]
    public void GetStatusText_DefaultStart_IsEmpty()
    {
        // Act & Assert
        Assert.Equal(string.Empty, MatchStatusPresenter.GetStatusText(new Game()));
    }

    [Theory]
    [InlineData(MatchPhase.Live, true)]
    [InlineData(MatchPhase.HalfTime, true)]
    [InlineData(MatchPhase.ExtraTime, true)]
    [InlineData(MatchPhase.Penalties, true)]
    [InlineData(MatchPhase.Upcoming, false)]
    [InlineData(MatchPhase.FullTime, false)]
    [InlineData(MatchPhase.Postponed, false)]
    public void IsLivePhase_CoversAllPhases(MatchPhase phase, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, MatchStatusPresenter.IsLivePhase(phase));
    }

    [Fact]
    public void GetScoreText_NoScore_ShowsVs()
    {
        // Act & Assert
        Assert.Equal("VS", MatchStatusPresenter.GetScoreText(new Game()));
        Assert.False(MatchStatusPresenter.HasScore(new Game()));
    }

    [Fact]
    public void GetScoreText_PartialScore_DefaultsMissingSideToZero()
    {
        // Arrange
        var game = new Game { HomeScore = 2 };

        // Act & Assert
        Assert.Equal("2 - 0", MatchStatusPresenter.GetScoreText(game));
        Assert.True(MatchStatusPresenter.HasScore(game));
    }

    [Fact]
    public void GetAggregateText_RequiresBothLegs()
    {
        // Arrange
        var both = new Game { AggregateHomeScore = 1, AggregateAwayScore = 1 };
        var one = new Game { AggregateHomeScore = 1 };

        // Act & Assert
        Assert.Equal("Agg 1-1", MatchStatusPresenter.GetAggregateText(both));
        Assert.Null(MatchStatusPresenter.GetAggregateText(one));
    }
}
