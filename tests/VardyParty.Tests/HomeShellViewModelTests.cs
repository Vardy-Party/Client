using System.Collections.Generic;
using AutoFixture;
using VardyParty.Catalog;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

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

        // Act
        sut.ClearSelection();

        // Assert
        Assert.Null(sut.SelectedGame);
        Assert.False(sut.UserInitiatedResolution);
        Assert.False(sut.IsSelected(game));
    }
}
