using Xunit;
using VardyParty.Presentation;

namespace VardyParty.Presentation.Tests;

public class PlaybackExitFocusTests
{
    [Fact]
    public void Choose_WatchedGameStillListed_SelectsThatCard()
    {
        var rails = new[]
        {
            new[] { "A|B", "C|D" },
            new[] { "E|F", "G|H" },
        };

        var choice = PlaybackExitFocus.Choose("E|F", rails);

        Assert.NotNull(choice);
        Assert.True(choice.Value.WatchedGame);
        Assert.Equal(1, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_WatchedGameFinished_SelectsFirstCardOfFirstRail()
    {
        var rails = new[]
        {
            new[] { "A|B", "C|D" },
            new[] { "E|F" },
        };

        var choice = PlaybackExitFocus.Choose("GONE|GAME", rails);

        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(0, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_NoWatchedGame_SelectsFirstCardOfFirstRail()
    {
        var rails = new[]
        {
            new[] { "A|B" },
        };

        var choice = PlaybackExitFocus.Choose(null, rails);

        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(0, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_EmptyFirstRail_SelectsFirstCardOfNextRail()
    {
        var rails = new[]
        {
            System.Array.Empty<string>(),
            new[] { "E|F", "G|H" },
        };

        var choice = PlaybackExitFocus.Choose("GONE|GAME", rails);

        Assert.NotNull(choice);
        Assert.False(choice.Value.WatchedGame);
        Assert.Equal(1, choice.Value.RailIndex);
        Assert.Equal(0, choice.Value.CardIndex);
    }

    [Fact]
    public void Choose_EmptyBoard_SelectsNothing()
    {
        var choice = PlaybackExitFocus.Choose("A|B", new[] { System.Array.Empty<string>() });

        Assert.Null(choice);
    }
}
