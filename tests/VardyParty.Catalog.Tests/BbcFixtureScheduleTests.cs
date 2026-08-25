using System;
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Tests;

public class BbcFixtureScheduleTests
{
    [Fact]
    public void RollingWindow_At11AmBst_IncludesPageFor2AmTwoNightsAhead()
    {
        // Arrange
        // 11:00 BST on 13 June 2026 = 10:00 UTC. A 02:00 BST kickoff on 15 June sits on the 15 June fixture page.
        var utcNow = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);

        // Act
        var dates = BbcFixtureSchedule.GetRollingWindowPageDates(utcNow);

        // Assert
        Assert.Contains(new DateOnly(2026, 6, 13), dates);
        Assert.Contains(new DateOnly(2026, 6, 14), dates);
        Assert.Contains(new DateOnly(2026, 6, 15), dates);
    }

    [Fact]
    public void RollingWindow_LateEvening_IncludesTomorrowPage()
    {
        // Arrange
        var utcNow = new DateTime(2026, 6, 12, 22, 0, 0, DateTimeKind.Utc);

        // Act
        var dates = BbcFixtureSchedule.GetRollingWindowPageDates(utcNow);

        // Assert
        Assert.Contains(new DateOnly(2026, 6, 12), dates);
        Assert.Contains(new DateOnly(2026, 6, 13), dates);
        Assert.Contains(new DateOnly(2026, 6, 14), dates);
    }

    [Fact]
    public void RollingWindow_Before2Am_IncludesYesterdayPage()
    {
        // Arrange
        // 01:30 BST on 14 June = 00:30 UTC
        var utcNow = new DateTime(2026, 6, 14, 0, 30, 0, DateTimeKind.Utc);

        // Act
        var dates = BbcFixtureSchedule.GetRollingWindowPageDates(utcNow);

        // Assert
        Assert.Contains(new DateOnly(2026, 6, 13), dates);
        Assert.Contains(new DateOnly(2026, 6, 14), dates);
    }

    [Fact]
    public void LookAheadEnd_Covers2AmBstOnDayAfterTomorrow()
    {
        // Arrange
        var utcNow = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        var kickoff = new DateTime(2026, 6, 15, 1, 0, 0, DateTimeKind.Utc); // 02:00 BST

        // Act
        var withinWindow = BbcFixtureSchedule.IsWithinLookAheadWindow(kickoff, utcNow);

        // Assert
        Assert.True(withinWindow);
    }

    [Fact]
    public void LookAheadEnd_ExcludesKickoffAfterWindow()
    {
        // Arrange
        var utcNow = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
        var kickoff = new DateTime(2026, 6, 15, 2, 30, 0, DateTimeKind.Utc); // 03:30 BST

        // Act
        var withinWindow = BbcFixtureSchedule.IsWithinLookAheadWindow(kickoff, utcNow);

        // Assert
        Assert.False(withinWindow);
    }
}
