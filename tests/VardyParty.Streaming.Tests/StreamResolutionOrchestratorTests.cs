using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamResolutionOrchestratorTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    public StreamResolutionOrchestratorTests()
    {
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    [Fact]
    public async Task StartAsync_LocalServiceUnavailable_AbortsFastWithLocalServiceUnavailable()
    {
        // Arrange
        var game = _fixture.Create<Game>();
        var launcher = _fixture.GetMock<IPlaybackLauncher>();
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game, launcher.Object);

        // Assert
        Assert.True(outcome.LocalServiceUnavailable);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Verify(c => c.InitializeAsync(It.IsAny<Game>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
    public async Task StartAsync_OverlappingCall_RefusesWithStartRefused()
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

        var initEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(async (Game _, CancellationToken ct) =>
            {
                initEntered.TrySetResult();
                await releaseInit.Task.WaitAsync(ct);
            });
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>());

        var player = _fixture.GetMock<INativeVideoPlayerService>();
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var first = sut.StartAsync(game, player.Object);
        await initEntered.Task;
        var overlapping = await sut.StartAsync(game, player.Object);
        releaseInit.TrySetResult();
        var firstOutcome = await first;

        // Assert: the refused call must say so — a silent empty outcome left
        // hosts with a latched selection and no banner (WSL field dead-end).
        Assert.True(overlapping.StartRefused);
        Assert.False(overlapping.NoWorkingStreams);
        Assert.Null(overlapping.PlaybackResult);
        Assert.True(firstOutcome.NoWorkingStreams);
        Assert.False(firstOutcome.StartRefused);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Verify(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_CancelledDuringResolve_ReleasesStartGateForNextStart()
    {
        // Arrange — Cancel while resolve is blocked must free the start-gate so
        // the next pick is not StartRefused.
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
            .With(s => s.Url, "https://streams.example.test/match.html")
            .With(s => s.Channel, "Channel North")
            .Create();

        var switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(switching);

        var resolveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
                BlockedYield(resolveEntered, ct));

        using var cts = new CancellationTokenSource();
        var player = _fixture.GetMock<INativeVideoPlayerService>();
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var first = sut.StartAsync(game, player.Object, cts.Token);
        await resolveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>());
        var second = await sut.StartAsync(game, player.Object);

        Assert.False(second.StartRefused);
        Assert.True(second.NoWorkingStreams);
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

    [Fact]
    public async Task StartAsync_FasterLaterCandidate_PlaysEarlierCatalogIndex()
    {
        // Arrange
        const string earlierUrl = "https://cdn.example.com/live/north.m3u8";
        const string laterUrl = "https://cdn.example.com/live/south.m3u8";
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

        var earlierStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl + "#north")
            .With(s => s.Channel, "Channel North")
            .Create();
        var laterStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl + "#south")
            .With(s => s.Channel, "Channel South")
            .Create();

        var earlier = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, earlierStream)
            .With(e => e.ResolvedM3U8Url, earlierUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, pageUrl)
            .Create();
        var later = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, laterStream)
            .With(e => e.ResolvedM3U8Url, laterUrl)
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
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, earlierStream).Create(),
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, laterStream).Create()
            });

        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => Yield(later, earlier));

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
            .ReturnsAsync(PlaybackResult.SuccessResult("Playing"));

        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game, player.Object);

        // Assert
        Assert.True(outcome.PlaybackResult?.Success);
        Assert.Equal(earlierUrl, switching.GetCurrentStream()?.ResolvedM3U8Url);
        player.Verify(
            p => p.PlayVideoAsync(
                earlierUrl,
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
                laterUrl,
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

    private static async IAsyncEnumerable<EnrichedStream> BlockedYield(
        TaskCompletionSource entered,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
