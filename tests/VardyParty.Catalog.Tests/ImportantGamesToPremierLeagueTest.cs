using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Configuration;
using VardyParty.Models;
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Tests;

public class ImportantGamesToPremierLeagueTest
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task ImportantGames_MapTo_BbcLeague()
    {
        // Arrange
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "Important Games";
        var bbcFixture = _fixture.Create<BbcFixture>() with
        {
            Home = game.Home,
            Away = game.Away,
            League = "League Alpha",
            KickoffUtc = DateTime.UtcNow
        };

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([bbcFixture]);

        var svc = new EnrichedGameService(
            api.Object,
            bbc.Object,
            _fixture.Create<GameMatcher>(),
            Options.Create(_fixture.Create<BbcFixturesSettings>()),
            Options.Create(_fixture.Create<GamesApiSettings>()),
            NullLogger<EnrichedGameService>.Instance);

        var emissions = new List<Dictionary<string, List<Game>>>();
        Exception? streamError = null;
        var subscription = svc.GamesStream.Subscribe(
            g => { if (g != null && g.Count > 0) emissions.Add(g); },
            ex => streamError = ex
        );

        try
        {
            // Act
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
            Assert.True(result.ContainsKey(league), $"Expected '{league}' key but got: {string.Join(", ", result.Keys)}");

            var enriched = result[league].First();
            Assert.Equal(game.Home, enriched.Home);
            Assert.Equal(game.Away, enriched.Away);
            Assert.Equal(bbcFixture.League, enriched.BBCLeague);
        }
        finally
        {
            subscription?.Dispose();
            svc?.Dispose();
        }
    }
}
