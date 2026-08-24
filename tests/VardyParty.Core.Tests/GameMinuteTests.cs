using AutoFixture;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests;

public class GameMinuteTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Theory]
    [InlineData("45+2'", 4502)]
    [InlineData("90+7'", 9007)]
    [InlineData("12'", 12)]
    public void MinuteFromStatus_ParsesEncoded(string status, int expected)
    {
        // Arrange
        var g = _fixture.Build<Game>()
            .With(game => game.StatusText, status)
            .Create();
        var minuteProperty = typeof(Game).GetProperty("MinuteFromStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var minuteFromStatus = minuteProperty.GetValue(g);

        // Assert
        Assert.Equal(expected, minuteFromStatus);
    }

    [Fact]
    public void DisplayStatusText_FormatsEncodedProperly()
    {
        // Arrange
        var g = _fixture.Build<Game>()
            .With(game => game.IsInProgress, true)
            .With(game => game.IsFinished, false)
            .With(game => game.IsHalfTime, false)
            .With(game => game.StatusText, string.Empty)
            .With(game => game.Minute, 9004)
            .Create();

        // Act
        var display = g.DisplayStatusText();

        // Assert
        Assert.Equal("90+4'", display);
    }

    [Fact]
    public void LiveMinuteForOrdering_SortsCorrectly()
    {
        // Arrange
        var g1 = _fixture.Build<Game>()
            .With(game => game.IsInProgress, true)
            .With(game => game.IsFinished, false)
            .With(game => game.IsHalfTime, false)
            .With(game => game.Minute, 9004)
            .Create();
        var g2 = _fixture.Build<Game>()
            .With(game => game.IsInProgress, true)
            .With(game => game.IsFinished, false)
            .With(game => game.IsHalfTime, false)
            .With(game => game.Minute, 70)
            .Create();

        // Act
        var laterThanEarlier = g1.LiveMinuteForOrdering > g2.LiveMinuteForOrdering;

        // Assert
        Assert.True(laterThanEarlier);
    }
}
