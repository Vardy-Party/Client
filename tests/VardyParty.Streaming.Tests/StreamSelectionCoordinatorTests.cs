using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Models;
using VardyParty.Streaming;
using Xunit;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Tests;

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
                    Confidence = "low",
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
                Confidence = "low",
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
                Confidence = "medium",
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
