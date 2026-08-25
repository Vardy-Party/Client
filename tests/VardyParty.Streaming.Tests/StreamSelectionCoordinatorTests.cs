using System.Collections.Generic;
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
}
