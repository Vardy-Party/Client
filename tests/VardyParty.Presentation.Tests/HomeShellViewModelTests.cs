using System.Collections.Generic;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Presentation.Tests;

public class HomeShellViewModelTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void RebindGames_WithoutPick_DoesNotSelectFirstCard()
    {
        // Arrange
        var sut = new HomeShellViewModel();
        var first = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();

        // Act
        sut.RebindGames(new List<Game> { first });

        // Assert
        Assert.Null(sut.SelectedGame);
        Assert.False(sut.UserInitiatedResolution);
    }

    [Fact]
    public void OnUserPicked_ThenRebind_KeepsSameMatchOnNewInstance()
    {
        // Arrange
        var sut = new HomeShellViewModel();
        var picked = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var refreshed = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        sut.OnUserPicked(picked);

        // Act
        sut.RebindGames(new List<Game> { refreshed });

        // Assert
        Assert.Same(refreshed, sut.SelectedGame);
        Assert.True(sut.UserInitiatedResolution);
        Assert.True(sut.IsSelected(refreshed));
    }

    [Fact]
    public void DecideResumeAfterPlayer_AfterPick_ResumesWhenCurrentIsSameInstance()
    {
        // Arrange
        var sut = new HomeShellViewModel();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        sut.OnUserPicked(game);
        sut.MarkPlayerSessionStarted();

        // Act
        var action = sut.DecideResumeAfterPlayer(
            isResolvingStreams: false,
            currentGame: game,
            resolutionExhausted: false);

        // Assert
        Assert.Equal(ResumeAfterPlayerAction.Resume, action);
    }

    [Fact]
    public void ClearSelection_DropsPickAndResumeIntent()
    {
        // Arrange
        var sut = new HomeShellViewModel();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        sut.OnUserPicked(game);
        sut.MarkPlayerSessionStarted();

        // Act
        sut.ClearSelection();

        // Assert
        Assert.Null(sut.SelectedGame);
        Assert.False(sut.UserInitiatedResolution);
        Assert.False(sut.PlayerSessionStarted);
        Assert.False(sut.IsSelected(game));
    }

    [Fact]
    public void OnUserPicked_AfterPlayerStarted_ResetsSessionFlag()
    {
        // Arrange
        var sut = new HomeShellViewModel();
        var first = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .Create();
        var second = _fixture.Build<Game>()
            .With(g => g.Home, "North Rovers")
            .With(g => g.Away, "South Athletic")
            .Create();
        sut.OnUserPicked(first);
        sut.MarkPlayerSessionStarted();

        // Act
        sut.OnUserPicked(second);

        // Assert
        Assert.Same(second, sut.SelectedGame);
        Assert.True(sut.UserInitiatedResolution);
        Assert.False(sut.PlayerSessionStarted);
    }
}
