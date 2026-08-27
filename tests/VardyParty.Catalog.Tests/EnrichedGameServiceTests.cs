using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Catalog;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests;

/// <summary>
/// The enriched-first initial publish contract: the FIRST board the UI sees
/// has BBC enrichment matched in. API-driven publishes are held while the
/// initial BBC fetch is in flight; an initial BBC failure releases the
/// API-only board immediately, and the enrichment valve (measured from
/// polling start) releases it if BBC hangs. Steady state publishes
/// immediately once the initial BBC fetch has completed.
/// </summary>
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

        var svc = CreateService(api, bbc);

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

        var svc = CreateService(api, bbc);

        Dictionary<string, List<Game>>? current = null;
        svc.GamesStream.Subscribe(g => current = g);

        // Act
        await Task.Delay(500);

        // Assert
        Assert.Null(current);
    }

    [Fact]
    public async Task StartBackgroundPolling_HoldsInitialPublishUntilBbcCompletes_SingleEnrichedBoard()
    {
        // Arrange: API answers immediately, BBC is held — the startup burst
        // must reach the UI as ONE initial ENRICHED board (published when BBC
        // completes), never an API-only board followed by a full reset.
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

        var svc = CreateService(api, bbc);

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

            // While the initial BBC fetch is in flight (and within the valve),
            // the API-first publish is held: nothing emits.
            await Task.Delay(1000);
            Assert.Empty(emissions);

            bbcHold.TrySetResult([]);

            for (var i = 0; i < 50 && emissions.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(300); // settle: catch any spurious second publish

            // Assert: exactly one initial board.
            var emission = Assert.Single(emissions);
            Assert.True(emission.ContainsKey(league));
            Assert.Single(emission[league]);
        }
        finally
        {
            bbcHold.TrySetResult([]);
            subscription.Dispose();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task StartBackgroundPolling_RepeatedApiPollsBeforeBbc_AreAllHeld()
    {
        // Arrange: a 1s API refresh delivers several boards before BBC
        // completes — the old one-shot skip leaked the second API poll as an
        // un-enriched publish; the new contract holds EVERY API publish until
        // the initial BBC fetch lands.
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Harbour FC")
            .With(g => g.Away, "Valley SC")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "Cup Delta";
        var bbcHold = new TaskCompletionSource<List<BbcFixture>>(TaskCreationOptions.RunContinuationsAsynchronously);

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .Returns(bbcHold.Task);

        var svc = CreateService(api, bbc, apiRefreshSeconds: 1);

        var emissions = new List<Dictionary<string, List<Game>>>();
        var subscription = svc.GamesStream.Subscribe(g =>
        {
            if (g != null && g.Count > 0)
                emissions.Add(g);
        });

        try
        {
            // Act: let at least two API polls complete while BBC is in flight.
            svc.StartBackgroundPolling();
            await Task.Delay(2500);

            // Assert: every API publish was held; releasing BBC publishes one
            // enriched board.
            Assert.Empty(emissions);
            api.Verify(x => x.GetAllGamesAsync(It.IsAny<bool>()), Times.AtLeast(2));

            bbcHold.TrySetResult([]);
            for (var i = 0; i < 50 && emissions.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(300);
            Assert.Single(emissions);
        }
        finally
        {
            bbcHold.TrySetResult([]);
            subscription.Dispose();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task StartBackgroundPolling_InitialBbcFailure_ReleasesHeldApiBoard()
    {
        // Arrange: the very first BBC fetch fails after the API board was
        // held — the failure must release the API-only board immediately (the
        // homepage must not wait out the valve because fixtures are down).
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "East Rovers")
            .With(g => g.Away, "West Wanderers")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "League Gamma";
        var bbcHold = new TaskCompletionSource<List<BbcFixture>>(TaskCreationOptions.RunContinuationsAsynchronously);

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .Returns(bbcHold.Task);

        var svc = CreateService(api, bbc);

        var emissions = new List<Dictionary<string, List<Game>>>();
        var subscription = svc.GamesStream.Subscribe(g =>
        {
            if (g != null && g.Count > 0)
                emissions.Add(g);
        });

        try
        {
            // Act: let the API board get held, then fail the BBC fetch.
            svc.StartBackgroundPolling();
            await Task.Delay(500);
            bbcHold.TrySetException(new Exception("BBC fixtures down"));

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
            bbcHold.TrySetException(new Exception("BBC fixtures down"));
            subscription.Dispose();
            svc.Dispose();
        }
    }

    [Fact]
    public async Task StartBackgroundPolling_BbcNeverCompletes_ValveReleasesApiBoard()
    {
        // Arrange: a BBC fetch that hangs forever (no success, no failure)
        // must not hold the homepage hostage — the enrichment valve (armed at
        // polling start; shortened here to keep the test fast) releases the
        // un-enriched API board.
        var api = _fixture.GetMock<IGamesCatalogApi>();
        var bbc = _fixture.GetMock<IBbcFixturesService>();
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Old Town")
            .With(g => g.Away, "New Town")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();
        const string league = "League Delta";
        var valve = TimeSpan.FromSeconds(2);
        var bbcHold = new TaskCompletionSource<List<BbcFixture>>(TaskCreationOptions.RunContinuationsAsynchronously);

        api.Setup(x => x.GetAllGamesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new Dictionary<string, List<Game>> { [league] = [game] });
        bbc.Setup(x => x.GetRollingWindowFixturesAsync(It.IsAny<CancellationToken>()))
            .Returns(bbcHold.Task);

        var svc = CreateService(api, bbc, valve: valve);

        var emissions = new List<Dictionary<string, List<Game>>>();
        var subscription = svc.GamesStream.Subscribe(g =>
        {
            if (g != null && g.Count > 0)
                emissions.Add(g);
        });

        try
        {
            // Act: never complete BBC; wait past the valve.
            svc.StartBackgroundPolling();

            var deadline = valve + TimeSpan.FromSeconds(5);
            for (var waited = TimeSpan.Zero; waited < deadline && emissions.Count == 0; waited += TimeSpan.FromMilliseconds(100))
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

    [Fact]
    public void InitialEnrichmentValve_IsTenSecondsFromPollingStart()
    {
        // Arrange: the spec'd fallback for a hung BBC endpoint.

        // Act
        var valve = EnrichedGameService.InitialEnrichmentValve;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(10), valve);
    }

    private EnrichedGameService CreateService(
        Mock<IGamesCatalogApi> api,
        Mock<IBbcFixturesService> bbc,
        int? apiRefreshSeconds = null,
        TimeSpan? valve = null)
    {
        var apiSettings = _fixture.Create<GamesApiSettings>();
        if (apiRefreshSeconds.HasValue)
        {
            apiSettings.RefreshSchedule = apiRefreshSeconds.Value;
        }

        return new EnrichedGameService(
            api.Object,
            bbc.Object,
            _fixture.Create<GameMatcher>(),
            Options.Create(_fixture.Create<BbcFixturesSettings>()),
            Options.Create(apiSettings),
            NullLogger<EnrichedGameService>.Instance,
            valve);
    }
}
