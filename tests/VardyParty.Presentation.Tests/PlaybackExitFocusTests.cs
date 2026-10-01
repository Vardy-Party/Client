using Xunit;
using VardyParty.Presentation;

namespace VardyParty.Presentation.Tests;

public class PlaybackExitFocusTests
{
    [Fact]
    public void Choose_WatchedGameStillListed_SelectsThatCard()
    {
        // Arrange
        var rails = new[]
        {
            new[] { "A|B", "C|D" },
            new[] { "E|F", "G|H" },
        };

        // Act
        var choice = PlaybackExitFocus.Choose("E|F", rails);

        // Assert
        Assert.NotNull(choice);
        Assert.True(choice.Value.WatchedGame);
        Assert.Equal(1, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_WatchedGameFinished_SelectsFirstCardOfFirstRail()
    {
        // Arrange
        var rails = new[]
        {
            new[] { "A|B", "C|D" },
            new[] { "E|F" },
        };

        // Act
        var choice = PlaybackExitFocus.Choose("GONE|GAME", rails);

        // Assert
        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(0, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_NoWatchedGame_SelectsFirstCardOfFirstRail()
    {
        // Arrange
        var rails = new[]
        {
            new[] { "A|B" },
        };

        // Act
        var choice = PlaybackExitFocus.Choose(null, rails);

        // Assert
        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(0, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_EmptyFirstRail_SelectsFirstCardOfNextRail()
    {
        // Arrange
        var rails = new[]
        {
            System.Array.Empty<string>(),
            new[] { "E|F", "G|H" },
        };

        // Act
        var choice = PlaybackExitFocus.Choose("GONE|GAME", rails);

        // Assert
        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(1, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_EmptyBoard_SelectsNothing()
    {
        // Arrange
        var rails = new[] { System.Array.Empty<string>() };

        // Act
        var choice = PlaybackExitFocus.Choose("A|B", rails);

        // Assert
        Assert.Null(choice);
    }
}
