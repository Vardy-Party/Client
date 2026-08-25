using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Health;
using VardyParty.Models;
using Xunit;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Tests;

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
        var outcome = await sut.StartAsync(game, player.Object);

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

    [Fact]
    public async Task StartAsync_NoCandidates_SetsNoWorkingStreamsAndDoesNotPlay()
    {
        // Arrange
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

        var switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(switching);

        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>());

        var player = _fixture.GetMock<INativeVideoPlayerService>();
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game, player.Object);

        // Assert
        Assert.True(outcome.NoWorkingStreams);
        player.Verify(
            p => p.PlayVideoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_WhenPlaybackReportsUserClosed_SetsUserClosed()
    {
        // Arrange
        const string m3u8Url = "https://cdn.example.com/live/north.m3u8";
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
            .With(e => e.ResolvedM3U8Url, m3u8Url)
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

        var player = _fixture.GetMock<INativeVideoPlayerService>();
        player.Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.Completed("User closed"));

        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game, player.Object);

        // Assert
        Assert.True(outcome.UserClosed);
        Assert.False(outcome.PlaybackResult?.Success);
    }

    [Fact]
    public async Task StartAsync_FirstHealthyInvokesPlayer_LaterHealthyJoinPoolOnly()
    {
        // Arrange
        const string firstUrl = "https://cdn.example.com/live/north.m3u8";
        const string secondUrl = "https://cdn.example.com/live/south.m3u8";
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

        var firstStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl + "#north")
            .With(s => s.Channel, "Channel North")
            .Create();
        var secondStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl + "#south")
            .With(s => s.Channel, "Channel South")
            .Create();

        var first = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, firstStream)
            .With(e => e.ResolvedM3U8Url, firstUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, pageUrl)
            .Create();
        var second = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, secondStream)
            .With(e => e.ResolvedM3U8Url, secondUrl)
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
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, firstStream).Create(),
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, secondStream).Create()
            });

        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => Yield(first, second));

        var playbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = _fixture.GetMock<INativeVideoPlayerService>();
        player.Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(() =>
            {
                playbackStarted.TrySetResult();
                return playbackGate.Task;
            });

        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var startTask = sut.StartAsync(game, player.Object);

        // Act
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var poolCountBeforePlaybackEnds = 0;
        for (var i = 0; i < 50 && poolCountBeforePlaybackEnds < 2; i++)
        {
            poolCountBeforePlaybackEnds = switching.GetHealthyStreams().Count;
            if (poolCountBeforePlaybackEnds < 2)
                await Task.Delay(20);
        }

        playbackGate.SetResult(PlaybackResult.SuccessResult("Playing"));
        var outcome = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(2, poolCountBeforePlaybackEnds);
        Assert.True(outcome.PlaybackResult?.Success);
        player.Verify(
            p => p.PlayVideoAsync(
                firstUrl,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
        player.Verify(
            p => p.PlayVideoAsync(
                secondUrl,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Never);
    }

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var stream in streams)
            yield return stream;
        await Task.CompletedTask;
    }
}
