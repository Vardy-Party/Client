using System.Collections.Generic;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Presentation.Tests;

public class HomePlaybackIntentTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void SameGame_MatchesHomeAndAwayIgnoringCase()
    {
        // Arrange
        var a = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var b = _fixture.Build<Game>()
            .With(g => g.Home, "home united")
            .With(g => g.Away, "AWAY CITY")
            .Create();

        // Act
        var same = HomePlaybackIntent.SameGame(a, b);

        // Assert
        Assert.True(same);
    }

    [Fact]
    public void SameGame_DifferentOpponents_IsNotTheSameMatch()
    {
        // Arrange
        var a = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var b = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Rival Town")
            .Create();

        // Act
        var same = HomePlaybackIntent.SameGame(a, b);

        // Assert
        Assert.False(same);
    }

    [Theory]
    [InlineData(true, false, true)]   // genuinely in-flight for the same game: swallow
    [InlineData(true, true, false)]   // same game but outcome delivered: re-click restarts
    [InlineData(false, false, false)] // different game: never swallowed
    [InlineData(false, true, false)]
    public void ShouldIgnoreRepick_OnlySwallowsInFlightSameGame(
        bool sameGame, bool resolutionExhausted, bool expected)
    {
        Assert.Equal(expected, HomePlaybackIntent.ShouldIgnoreRepick(sameGame, resolutionExhausted));
    }

    [Fact]
    public void RebindSelection_EmptyList_ClearsSelection()
    {
        // Arrange
        var selected = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var (reboundSelected, current) = HomePlaybackIntent.RebindSelection(new List<Game>(), selected);

        // Assert
        Assert.Null(reboundSelected);
        Assert.Null(current);
    }

    [Fact]
    public void RebindSelection_NoPriorChoice_DoesNotAutoSelectFirstCard()
    {
        // Arrange
        var first = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var games = new List<Game> { first };

        // Act
        var (selected, current) = HomePlaybackIntent.RebindSelection(games, selectedGame: null);

        // Assert
        Assert.Null(selected);
        Assert.Null(current);
    }

    [Fact]
    public void RebindSelection_ExplicitChoice_RebindsOntoRefreshedInstance()
    {
        // Arrange
        var previous = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var refreshed = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var other = _fixture.Build<Game>()
            .With(g => g.Home, "North Rovers")
            .With(g => g.Away, "South Athletic")
            .Create();
        var games = new List<Game> { other, refreshed };

        // Act
        var (selected, current) = HomePlaybackIntent.RebindSelection(games, previous);

        // Assert
        Assert.Same(refreshed, selected);
        Assert.Same(refreshed, current);
    }

    [Fact]
    public void DecideResumeAfterPlayer_WithoutUserClick_DoesNotResume()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: game,
            currentGame: game,
            resolutionExhausted: false);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.None, action);
    }

    [Fact]
    public void DecideResumeAfterPlayer_AfterClickAndSameInstance_Resumes()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkUserInitiated();
        sut.MarkPlayerSessionStarted();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: game,
            currentGame: game,
            resolutionExhausted: false);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.Resume, action);
    }

    [Fact]
    public void DecideResumeAfterPlayer_AfterClickBeforePlayer_DoesNotResume()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkUserInitiated();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: game,
            currentGame: game,
            resolutionExhausted: false);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.None, action);
    }

    [Fact]
    public void DecideResumeAfterPlayer_WhenExhausted_Clears()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkUserInitiated();
        sut.MarkPlayerSessionStarted();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: game,
            currentGame: game,
            resolutionExhausted: true);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.Clear, action);
    }

    [Fact]
    public void DecideResumeAfterPlayer_WhenCurrentGameDetached_Clears()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkUserInitiated();
        sut.MarkPlayerSessionStarted();
        var selected = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var current = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: selected,
            currentGame: current,
            resolutionExhausted: false);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.Clear, action);
    }

    [Fact]
    public void IsSelected_TracksExplicitChoiceNotFirstCard()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        var chosen = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var firstCard = _fixture.Build<Game>()
            .With(g => g.Home, "North Rovers")
            .With(g => g.Away, "South Athletic")
            .Create();

        // Act
        var chosenSelected = sut.IsSelected(chosen, chosen);
        var firstCardSelected = sut.IsSelected(firstCard, chosen);

        // Assert
        Assert.True(chosenSelected);
        Assert.False(firstCardSelected);
    }

    [Fact]
    public void MarkPlayerSessionStarted_WithoutUserClick_DoesNotResume()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkPlayerSessionStarted();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            selectedGame: game,
            currentGame: game,
            resolutionExhausted: false);

        // Assert
        Assert.False(sut.UserInitiatedResolution);
        Assert.True(sut.PlayerSessionStarted);
        Assert.Equal(ResumeAfterPlayerAction.None, action);
    }

    [Fact]
    public void MarkUserInitiated_ResetsPlayerSessionStarted()
    {
        // Arrange
        var sut = new HomePlaybackIntent();
        sut.MarkUserInitiated();
        sut.MarkPlayerSessionStarted();

        // Act
        sut.MarkUserInitiated();

        // Assert
        Assert.True(sut.UserInitiatedResolution);
        Assert.False(sut.PlayerSessionStarted);
    }
}
