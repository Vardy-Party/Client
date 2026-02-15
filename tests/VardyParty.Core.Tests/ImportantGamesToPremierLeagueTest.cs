using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Configuration;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class ImportantGamesToPremierLeagueTest
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task ImportantGames_MapTo_BbcPremierLeague_And_LogoIsUsed()
    {
        // Arrange
        var apiMock = new Mock<IApiService>();
        var bbcMock = new Mock<IBbcFixturesService>();

        var apiGames = new Dictionary<string, List<Game>>();
        var game = new Game { Home = "Real Madrid", Away = "Barcelona", Start = DateTime.UtcNow };
        apiGames["Important Games"] = new List<Game> { game };
        apiMock.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>())).ReturnsAsync(apiGames);

        // BBC fixture in Premier League (for test purposes)
        var bbcFixture = new BbcFixture("Real Madrid", "Barcelona", DateTime.UtcNow, "", false, false, false, null,
            null, null, string.Empty, string.Empty, "Premier League", false);
        bbcMock.Setup(x => x.GetFixturesAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<BbcFixture> { bbcFixture });

        var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
        var bbcFixturesSettings = _fixture.Create<BbcFixturesSettings>();
        var gamesApiSettings = _fixture.Create<GamesApiSettings>();

        var svc = new EnrichedGameService(apiMock.Object, bbcMock.Object, matcher, Options.Create(bbcFixturesSettings),
            Options.Create(gamesApiSettings), NullLogger<EnrichedGameService>.Instance);

        // Act
        // svc.RefreshAsync() was removed. Constructor starts polling.

        // Wait for background processing (constructor task)
        await Task.Delay(300);

        var result = await svc.GamesStream.FirstAsync(x => x != null);

        // Assert enrichment
        if (!result.ContainsKey("Important Games"))
            // If timing issue, fail gracefully or retry?
            Assert.Fail("Games not loaded in time");

        var enriched = result["Important Games"].First();
        Assert.Equal("Real Madrid", enriched.Home);
        Assert.Equal("Barcelona", enriched.Away);
        Assert.Equal("Premier League", enriched.BBCLeague);

        // Verify the logo mapping using the new LeagueLogoMapper
        var logoPath = LeagueLogoMapper.GetLogoForLeague(enriched);
        Assert.False(string.IsNullOrEmpty(logoPath));
        Assert.Contains("premier-league-logo", logoPath, StringComparison.OrdinalIgnoreCase);
    }
}