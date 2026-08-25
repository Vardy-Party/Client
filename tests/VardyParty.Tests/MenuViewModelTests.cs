using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using Moq;
using VardyParty.Catalog;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

public class MenuViewModelTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void RefreshKnownLeagues_UsesFilterService()
    {
        // Arrange
        var leagues = new List<string> { "League Alpha", "League Beta" };
        var games = new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = new List<Game>
            {
                _fixture.Build<Game>().With(g => g.Home, "Home United").With(g => g.Away, "Away City").Create()
            }
        };
        _fixture.GetMock<ILeagueFilterService>()
            .Setup(f => f.GetKnownLeagues(games))
            .Returns(leagues);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.RefreshKnownLeagues(games);

        // Assert
        Assert.Equal(leagues, sut.KnownLeagues);
    }

    [Fact]
    public void ToggleLeague_FlipsVisibility()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        filter.Setup(f => f.IsLeagueVisible("League Alpha")).Returns(false);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleLeague("League Alpha");

        // Assert
        filter.Verify(f => f.SetLeagueVisible("League Alpha", true), Times.Once);
    }

    [Fact]
    public void ShowAllLeagues_MakesKnownLeaguesVisible()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        filter.Setup(f => f.GetKnownLeagues(It.IsAny<IDictionary<string, List<Game>>?>()))
            .Returns(new List<string> { "League Alpha", "League Beta" });
        var sut = _fixture.Create<MenuViewModel>();
        sut.RefreshKnownLeagues(new Dictionary<string, List<Game>>());

        // Act
        sut.ShowAllLeagues();

        // Assert
        filter.Verify(
            f => f.SetLeaguesVisible(It.Is<IEnumerable<string>>(l => l.Contains("League Alpha") && l.Contains("League Beta")), true),
            Times.Once);
    }

    [Fact]
    public void ResetToDefaults_DelegatesToFilter()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ResetToDefaults();

        // Assert
        filter.Verify(f => f.ResetToDefaults(), Times.Once);
    }
}
