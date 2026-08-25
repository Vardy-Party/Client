using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamSelectionCoordinatorTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task InitializeAsync_ConcurrentCalls_DoNotThrowAndLeaveAConsistentQueue()
    {
        // Arrange
        var streams = new List<StreamModel>();
        for (var i = 0; i < 24; i++)
        {
            streams.Add(_fixture.Build<StreamModel>()
                .With(s => s.Url, $"https://streams.example.com/watch/{i}")
                .With(s => s.Channel, $"Channel {i}")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .With(s => s.PlayerStream, string.Empty)
                .With(s => s.StreamStatus, string.Empty)
                .Create());
        }

        var response = new StreamResponse { Streams = streams };
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .Returns(async (string _, string _, string _, bool _) =>
            {
                await Task.Delay(40);
                return response;
            });

        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string _, CancellationToken _) =>
            {
                await Task.Delay(40);
                return new RecommendationResponse
                {
                    Confidence = RecommendationConfidence.Low,
                    HasData = false
                };
            });

        var sut = _fixture.Create<StreamSelectionCoordinator>();
        var starts = new Task[8];

        // Act
        for (var i = 0; i < starts.Length; i++)
        {
            starts[i] = sut.InitializeAsync(game);
        }

        await Task.WhenAll(starts);

        // Assert
        Assert.Equal(24, sut.GetOrderedCandidates().Count);
    }

    [Fact]
    public async Task InitializeAsync_LowConfidenceRecommendation_TriesThatStreamFirst()
    {
        // Arrange
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/weak-alpha", "Channel Alpha"),
            Fb("https://streams.example.com/weak-bravo", "Channel Bravo"),
            Fb("https://streams.example.com/northgate", "Channel North")
        };
        var game = MatchGame();
        SetupCatalog(streams);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResponse
            {
                Confidence = RecommendationConfidence.Low,
                HasData = true,
                Recommended =
                [
                    new RecommendationItem { Url = "https://streams.example.com/northgate" }
                ]
            });
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        var ordered = sut.GetOrderedCandidates();
        Assert.Equal("Channel North", ordered[0].Stream.Channel);
        Assert.Equal(
            ["Channel North", "Channel Alpha", "Channel Bravo"],
            ordered.Select(c => c.Stream.Channel).ToList());
    }

    [Fact]
    public async Task InitializeAsync_RecommendedMpStream_IsTriedBeforeFbCatalogNeighbors()
    {
        // Arrange
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/alpha", "Channel Alpha"),
            Fb("https://streams.example.com/bravo", "Channel Bravo"),
            Mp("https://mpoutqn.example.com/northgate", "Channel North")
        };
        var game = MatchGame();
        SetupCatalog(streams);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResponse
            {
                Confidence = RecommendationConfidence.Medium,
                HasData = true,
                Recommended =
                [
                    new RecommendationItem { Url = "https://mpoutqn.example.com/northgate" }
                ]
            });
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal("Channel North", sut.GetOrderedCandidates()[0].Stream.Channel);
    }

    [Fact]
    public async Task InitializeAsync_HighThenLowThenUnbadgedRemainder_IsTryOrder()
    {
        // Arrange
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/catalog-other", "Channel Other"),
            Fb("https://streams.example.com/stale-strong", "Channel Stale"),
            Fb("https://streams.example.com/live-recent", "Channel Live")
        };
        var game = MatchGame();
        SetupCatalog(streams);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Recs(
                RecommendationConfidence.High,
                RecItem("https://streams.example.com/stale-strong", RecommendationConfidence.Low),
                RecItem("https://streams.example.com/live-recent", RecommendationConfidence.High)));
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal(
            ["Channel Live", "Channel Stale", "Channel Other"],
            sut.GetOrderedCandidates().Select(c => c.Stream.Channel).ToList());
    }

    [Fact]
    public async Task InitializeAsync_MediumConfidenceSitsBetweenHighAndLow()
    {
        // Arrange
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/low", "Channel Low"),
            Fb("https://streams.example.com/medium", "Channel Medium"),
            Fb("https://streams.example.com/high", "Channel High")
        };
        var game = MatchGame();
        SetupCatalog(streams);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Recs(
                RecommendationConfidence.High,
                RecItem("https://streams.example.com/low", RecommendationConfidence.Low),
                RecItem("https://streams.example.com/medium", RecommendationConfidence.Medium),
                RecItem("https://streams.example.com/high", RecommendationConfidence.High)));
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal(
            ["Channel High", "Channel Medium", "Channel Low"],
            sut.GetOrderedCandidates().Select(c => c.Stream.Channel).ToList());
    }

    [Fact]
    public async Task InitializeAsync_OverlappingGames_LastCompletedWins()
    {
        // Arrange
        var firstGame = MatchGame();
        var secondGame = _fixture.Build<Game>()
            .With(g => g.Home, "North Rovers")
            .With(g => g.Away, "South Athletic")
            .With(g => g.ApiLeague, "league-bravo")
            .With(g => g.League, "League Bravo")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                firstGame.ApiLeague, firstGame.Home, firstGame.Away, It.IsAny<bool>()))
            .Returns(async (string _, string _, string _, bool _) =>
            {
                await Task.Delay(40);
                return new StreamResponse { Streams = [Fb("https://streams.example.com/first", "Channel First")] };
            });
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                secondGame.ApiLeague, secondGame.Home, secondGame.Away, It.IsAny<bool>()))
            .Returns(async (string _, string _, string _, bool _) =>
            {
                await Task.Delay(40);
                return new StreamResponse { Streams = [Fb("https://streams.example.com/second", "Channel Second")] };
            });
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Recs(RecommendationConfidence.None));
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        var first = sut.InitializeAsync(firstGame);
        var second = sut.InitializeAsync(secondGame);
        await Task.WhenAll(first, second);

        // Assert
        var ordered = sut.GetOrderedCandidates();
        Assert.Equal("Channel Second", Assert.Single(ordered).Stream.Channel);
    }

    [Fact]
    public async Task InitializeAsync_RecommendationsThrow_FallsBackToFbBeforeMp()
    {
        // Arrange
        var streams = new List<StreamModel>
        {
            Mp("https://mpoutqn.example.com/east", "Channel East"),
            Fb("https://streams.example.com/north", "Channel North"),
            Fb("https://streams.example.com/south", "Channel South")
        };
        var game = MatchGame();
        SetupCatalog(streams);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("northgate recs down"));
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal(
            ["Channel North", "Channel South", "Channel East"],
            sut.GetOrderedCandidates().Select(c => c.Stream.Channel).ToList());
    }

    [Fact]
    public async Task InitializeAsync_EmptyCatalog_LeavesNoCandidates()
    {
        // Arrange
        var game = MatchGame();
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new StreamResponse { Streams = [] });
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Empty(sut.GetOrderedCandidates());
    }

    private Game MatchGame() =>
        _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

    private void SetupCatalog(List<StreamModel> streams)
    {
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new StreamResponse { Streams = streams });
    }

    private RecommendationItem RecItem(string url, RecommendationConfidence confidence) =>
        _fixture.Build<RecommendationItem>()
            .With(item => item.Url, url)
            .With(item => item.Confidence, confidence)
            .Without(item => item.StreamName)
            .Without(item => item.Meta)
            .Create();

    private RecommendationResponse Recs(
        RecommendationConfidence overall,
        params RecommendationItem[] items) =>
        _fixture.Build<RecommendationResponse>()
            .With(response => response.Confidence, overall)
            .With(response => response.HasData, items.Length > 0)
            .With(response => response.Recommended, items.ToList())
            .Create();

    private StreamModel Fb(string url, string channel) =>
        _fixture.Build<StreamModel>()
            .With(s => s.Url, url)
            .With(s => s.Channel, channel)
            .With(s => s.Source, "fb")
            .With(s => s.ResolutionStrategy, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.StreamStatus, string.Empty)
            .Create();

    private StreamModel Mp(string url, string channel) =>
        _fixture.Build<StreamModel>()
            .With(s => s.Url, url)
            .With(s => s.Channel, channel)
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStream, channel)
            .With(s => s.PlayerStreams, new List<string> { channel })
            .With(s => s.StreamStatus, string.Empty)
            .Create();
}
