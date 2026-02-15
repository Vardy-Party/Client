using System;
using System.Collections.Generic;
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

        bbcMock.Setup(x => x.GetFixturesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<BbcFixture>());

        var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
        var svc = new EnrichedGameService(apiMock.Object, bbcMock.Object, matcher,
            Options.Create(bbcFixturesSettings), Options.Create(gamesApiSettings),
            NullLogger<EnrichedGameService>.Instance);

        await Task.Delay(200);

        var result = await svc.GamesStream.FirstAsync(x => x != null);

        Assert.Single(result);
        Assert.Single(result["PL"]);
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

        await Task.Delay(200);

        Dictionary<string, List<Game>>? current = null;
        svc.GamesStream.Subscribe(g => current = g);

        await Task.Delay(200);

        Assert.Null(current);
    }
}