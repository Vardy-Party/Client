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

        // Collect emissions
        var emissions = new List<Dictionary<string, List<Game>>>();
        Exception streamError = null;
        var subscription = svc.GamesStream.Subscribe(
            g => { if (g != null && g.Count > 0) emissions.Add(g); },
            ex => streamError = ex
        );

        try
        {
            // Act - Start background polling and wait for emission
            svc.StartBackgroundPolling();
            
            for (int i = 0; i < 50 && emissions.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            if (streamError != null)
                throw new Exception($"Stream error: {streamError.Message}", streamError);

            // Assert
            Assert.NotEmpty(emissions);
            var result = emissions[0];
            
            Assert.NotEmpty(result);
            Assert.True(result.ContainsKey("Important Games"), $"Expected 'Important Games' key but got: {string.Join(", ", result.Keys)}");

            var enriched = result["Important Games"].First();
            Assert.Equal("Real Madrid", enriched.Home);
            Assert.Equal("Barcelona", enriched.Away);
            Assert.Equal("Premier League", enriched.BBCLeague);

            // Verify the logo mapping using the new LeagueLogoMapper
            var logoPath = LeagueLogoMapper.GetLogoForLeague(enriched);
            Assert.False(string.IsNullOrEmpty(logoPath));
            Assert.Contains("premier-league-logo", logoPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            subscription?.Dispose();
            svc?.Dispose();
        }
    }
}