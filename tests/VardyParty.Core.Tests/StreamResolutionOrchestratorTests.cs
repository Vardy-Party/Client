using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Services;
using Xunit;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Core.Tests;

public class StreamResolutionOrchestratorTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task StartAsync_CachedM3U8Fails_RetriesWithFreshUrl()
    {
        // Arrange
        const string cachedUrl = "https://cdn.example.com/live/cached.m3u8?token=old";
        const string freshUrl = "https://cdn.example.com/live/fresh.m3u8?token=new";
        const string pageUrl = "https://streams.example.com/match.html";

        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl)
            .With(s => s.Channel, "Channel North")
            .Create();

        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, cachedUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, pageUrl)
            .Create();

        var switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(switching);

        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()
            });

        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => Yield(enriched));

        _fixture.GetMock<IApiService>()
            .Setup(a => a.ResolveM3U8ForPlaybackAsync(
                stream,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshUrl);

        var player = _fixture.GetMock<INativeVideoPlayerService>();
        player.SetupSequence(p => p.PlayVideoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.Completed("CDN token expired", recoverable: true))
            .ReturnsAsync(PlaybackResult.SuccessResult("Playing"));

        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game);

        // Assert
        Assert.True(outcome.PlaybackResult?.Success);
        player.Verify(p => p.PlayVideoAsync(
            cachedUrl,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<Func<Task>?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
        player.Verify(p => p.PlayVideoAsync(
            freshUrl,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<Func<Task>?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
    }

    private static async IAsyncEnumerable<EnrichedStream> Yield(EnrichedStream stream)
    {
        yield return stream;
        await Task.CompletedTask;
    }
}
