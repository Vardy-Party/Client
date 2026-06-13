using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
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

public class EnrichedGameServiceTests
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task RefreshAsync_MergesApiAndBbc()
    {
        var apiMock = new Mock<IApiService>();
        var bbcMock = new Mock<IBbcFixturesService>();

        var bbcFixturesSettings = _fixture.Create<BbcFixturesSettings>();
        var gamesApiSettings = _fixture.Create<GamesApiSettings>();

        var games = new Dictionary<string, List<Game>>
        {
            ["PL"] = new() { new Game { Home = "TeamA", Away = "TeamB", Start = DateTime.UtcNow } }
        };
        apiMock.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>())).ReturnsAsync(games);

        bbcMock.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BbcFixture>());

        var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
        var svc = new EnrichedGameService(apiMock.Object, bbcMock.Object, matcher,
            Options.Create(bbcFixturesSettings), Options.Create(gamesApiSettings),
            NullLogger<EnrichedGameService>.Instance);

        // Collect emissions
        var emissions = new List<Dictionary<string, List<Game>>>();
        Exception streamError = null;
        var subscription = svc.GamesStream.Subscribe(
            g => { if (g != null && g.Count > 0) emissions.Add(g); },
            ex => streamError = ex
        );

        try
        {
            // Start the background polling
            svc.StartBackgroundPolling();
            
            // Wait for at least one emission
            for (int i = 0; i < 50 && emissions.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            if (streamError != null)
                throw new Exception($"Stream error: {streamError.Message}", streamError);

            Assert.NotEmpty(emissions);
            var result = emissions[0];
            
            Assert.NotEmpty(result);
            Assert.True(result.ContainsKey("PL"), $"Expected 'PL' key but got: {string.Join(", ", result.Keys)}");
            Assert.Single(result["PL"]);
        }
        finally
        {
            subscription?.Dispose();
            svc?.Dispose();
        }
    }

    [Fact]
    public async Task RefreshAsync_HandlesExceptionsGracefully()
    {
        var apiMock = new Mock<IApiService>();
        apiMock.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>())).ThrowsAsync(new Exception("Fail"));

        var bbcMock = new Mock<IBbcFixturesService>();
        var bbcFixturesSettings = _fixture.Create<BbcFixturesSettings>();
        var gamesApiSettings = _fixture.Create<GamesApiSettings>();

        var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
        var svc = new EnrichedGameService(apiMock.Object, bbcMock.Object, matcher, Options.Create(bbcFixturesSettings),
            Options.Create(gamesApiSettings),
            NullLogger<EnrichedGameService>.Instance);

        Dictionary<string, List<Game>>? current = null;
        svc.GamesStream.Subscribe(g => current = g);

        // Wait for stream to potentially emit or error
        await Task.Delay(500);

        Assert.Null(current);
    }
}