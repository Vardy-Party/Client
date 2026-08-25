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
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Tests;

public class EnrichedGameServiceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task RefreshAsync_MergesApiAndBbc()
    {
        // Arrange
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "League Alpha";

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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
            Assert.Single(result[league]);
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
        // Arrange
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>())).ThrowsAsync(_fixture.Create<Exception>());

        var svc = new EnrichedGameService(
            api.Object,
            bbc.Object,
            _fixture.Create<GameMatcher>(),
            Options.Create(_fixture.Create<BbcFixturesSettings>()),
            Options.Create(_fixture.Create<GamesApiSettings>()),
            NullLogger<EnrichedGameService>.Instance);

        Dictionary<string, List<Game>>? current = null;
        svc.GamesStream.Subscribe(g => current = g);

        // Act
        await Task.Delay(500);

        // Assert
        Assert.Null(current);
    }

    [Fact]
    public async Task StartBackgroundPolling_PublishesApiGamesBeforeBbcReturns()
    {
        // Arrange
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "North Athletic")
            .With(g => g.Away, "South Wanderers")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "League Beta";
        var bbcHold = new TaskCompletionSource<List<BbcFixture>>(TaskCreationOptions.RunContinuationsAsynchronously);

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .Returns(bbcHold.Task);

        var svc = new EnrichedGameService(
            api.Object,
            bbc.Object,
            _fixture.Create<GameMatcher>(),
            Options.Create(_fixture.Create<BbcFixturesSettings>()),
            Options.Create(_fixture.Create<GamesApiSettings>()),
            NullLogger<EnrichedGameService>.Instance);

        var emissions = new List<Dictionary<string, List<Game>>>();
        var subscription = svc.GamesStream.Subscribe(g =>
        {
            if (g != null && g.Count > 0)
                emissions.Add(g);
        });

        try
        {
            // Act
            svc.StartBackgroundPolling();

            for (var i = 0; i < 50 && emissions.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            // Assert
            Assert.NotEmpty(emissions);
            Assert.True(emissions[0].ContainsKey(league));
            Assert.Single(emissions[0][league]);
        }
        finally
        {
            bbcHold.TrySetResult([]);
            subscription.Dispose();
            svc.Dispose();
        }
    }
}
