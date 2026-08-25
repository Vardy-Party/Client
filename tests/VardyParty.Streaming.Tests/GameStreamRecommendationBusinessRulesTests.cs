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

public class GameStreamRecommendationBusinessRulesTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task InitializeAsync_ForAGame_FetchesThatMatchRecommendationsAndTriesHighThenLowThenUnbadged()
    {
        // Arrange — catalog order is unbadged first, like the control-panel tree.
        var game = MatchGame();
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/channel-failed", "Channel Failed"),
            Fb("https://streams.example.com/channel-quiet", "Channel Quiet"),
            Fb("https://streams.example.com/channel-stale", "Channel Stale"),
            Fb("https://streams.example.com/channel-live", "Channel Live")
        };
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<bool>()))
            .ReturnsAsync(new StreamResponse { Streams = streams });
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResponse
            {
                Confidence = "high",
                HasData = true,
                Recommended =
                [
                    new RecommendationItem
                    {
                        Url = "https://streams.example.com/channel-stale",
                        StreamName = "Channel Stale",
                        Confidence = "low"
                    },
                    new RecommendationItem
                    {
                        Url = "https://streams.example.com/channel-live",
                        StreamName = "Channel Live",
                        Confidence = "high"
                    }
                ]
            });
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal(
            ["Channel Live", "Channel Stale", "Channel Failed", "Channel Quiet"],
            sut.GetOrderedCandidates().Select(c => c.Stream.Channel).ToList());
        _fixture.GetMock<IStreamHealthService>().Verify(
            h => h.GetRecommendationsAsync(
                "league-alpha",
                "Home United",
                "Away City",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_ForAGame_PlacesMediumConfidenceBetweenHighAndLow()
    {
        // Arrange
        var game = MatchGame();
        var streams = new List<StreamModel>
        {
            Fb("https://streams.example.com/channel-low", "Channel Low"),
            Fb("https://streams.example.com/channel-medium", "Channel Medium"),
            Fb("https://streams.example.com/channel-high", "Channel High")
        };
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetStreamsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<bool>()))
            .ReturnsAsync(new StreamResponse { Streams = streams });
        _fixture.GetMock<IStreamHealthService>()
            .Setup(h => h.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResponse
            {
                Confidence = "high",
                HasData = true,
                Recommended =
                [
                    new RecommendationItem
                    {
                        Url = "https://streams.example.com/channel-low",
                        Confidence = "low"
                    },
                    new RecommendationItem
                    {
                        Url = "https://streams.example.com/channel-medium",
                        Confidence = "medium"
                    },
                    new RecommendationItem
                    {
                        Url = "https://streams.example.com/channel-high",
                        Confidence = "high"
                    }
                ]
            });
        var sut = _fixture.Create<StreamSelectionCoordinator>();

        // Act
        await sut.InitializeAsync(game);

        // Assert
        Assert.Equal(
            ["Channel High", "Channel Medium", "Channel Low"],
            sut.GetOrderedCandidates().Select(c => c.Stream.Channel).ToList());
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

    private StreamModel Fb(string url, string channel) =>
        _fixture.Build<StreamModel>()
            .With(s => s.Url, url)
            .With(s => s.Channel, channel)
            .With(s => s.Source, "fb")
            .With(s => s.ResolutionStrategy, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.StreamStatus, string.Empty)
            .Create();
}
