using System;
using System.Collections.Generic;
using System.Linq;
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
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(false);
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
            .Without(e => e.RequestHeaders)
            .Without(e => e.RewrittenSegments)
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

        var freshResponse = _fixture.Build<M3U8Response>()
            .With(r => r.Url, freshUrl)
            .With(r => r.RequestHeaders, (Dictionary<string, string>?)null)
            .With(r => r.RewrittenSegments, (List<string>?)null)
            .Create();
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(
                stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshResponse);

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
    public async Task StartAsync_CancelledPredecessorStillUnwinding_WaitsInsteadOfStartRefused()
    {
        // The previous session's token is cancelled, but it keeps the gate while
        // the abort unwinds (longer than the 2s overlap refusal). The replacement
        // start must wait on its own token and run, not return StartRefused.
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

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwindStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(async (Game _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return;

                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    unwindStarted.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    throw;
                }
            });
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>());

        using var cts = new CancellationTokenSource();
        var player = _fixture.GetMock<INativeVideoPlayerService>();
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        var first = sut.StartAsync(game, player.Object, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await unwindStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = sut.StartAsync(game, player.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.False(secondOutcome.StartRefused);
        Assert.True(secondOutcome.NoWorkingStreams);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Verify(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StartAsync_MpChipSiblings_JoinPoolBeforeRemainingCatalogFinishes()
    {
        // Arrange — seed MP plays; sibling chips must reach the healthy pool while
        // a later catalog row is still unresolved (not wait for full catalog).
        const string seedUrl = "https://cdn.example.com/live/chip-a.m3u8";
        const string siblingUrl = "https://cdn.example.com/live/chip-b.m3u8";
        const string laterUrl = "https://cdn.example.com/live/later.m3u8";
        const string pageUrl = "https://streams.example.com/mp-match.html";

        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

        var seedStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl)
            .With(s => s.Channel, "Chip A")
            .With(s => s.PlayerStream, "Chip A")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.Source, "mp")
            .Create();
        var laterStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl + "#later")
            .With(s => s.Channel, "Channel South")
            .Create();
        var siblingStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl)
            .With(s => s.Channel, "Chip B")
            .With(s => s.PlayerStream, "Chip B")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.Source, "mp")
            .Create();

        var seed = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, seedStream)
            .With(e => e.ResolvedM3U8Url, seedUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, pageUrl)
            .Create();
        var sibling = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, siblingStream)
            .With(e => e.ResolvedM3U8Url, siblingUrl)
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
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, seedStream).Create(),
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, laterStream).Create()
            });

        var siblingsYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => YieldSeedSiblingThenLater(seed, sibling, later, siblingsYielded, releaseLater.Task));

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
        await siblingsYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var poolCountBeforeLaterCatalog = 0;
        for (var i = 0; i < 50 && poolCountBeforeLaterCatalog < 2; i++)
        {
            poolCountBeforeLaterCatalog = switching.GetHealthyStreams().Count;
            if (poolCountBeforeLaterCatalog < 2)
                await Task.Delay(20);
        }

        releaseLater.TrySetResult();
        playbackGate.SetResult(PlaybackResult.SuccessResult("Playing"));
        var outcome = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(2, poolCountBeforeLaterCatalog);
        Assert.True(outcome.PlaybackResult?.Success);
        player.Verify(
            p => p.PlayVideoAsync(
                seedUrl,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
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
    public async Task StartAsync_SlowMpAtIndexZero_DoesNotHoldHealthyFbFirstPlay()
    {
        // Arrange - no recs: MP is tried first, but /mp is slow; a healthy FB
        // at index 1 must open without waiting, and the MP joins the pool after.
        const string fbUrl = "https://cdn.example.com/live/south.m3u8";
        const string mpUrl = "https://cdn.example.com/live/chip-a.m3u8";
        const string fbPage = "https://streams.example.com/match.html#south";
        const string mpPage = "https://streams.example.com/mp-match.html";

        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

        var mpStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, mpPage)
            .With(s => s.Channel, "Chip A")
            .With(s => s.PlayerStream, "Chip A")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.Source, "mp")
            .Create();
        var fbStream = _fixture.Build<StreamModel>()
            .With(s => s.Url, fbPage)
            .With(s => s.Channel, "Channel South")
            .With(s => s.ResolutionStrategy, "v1")
            .With(s => s.Source, "fb")
            .Create();

        var fb = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, fbStream)
            .With(e => e.ResolvedM3U8Url, fbUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, fbPage)
            .Create();
        var mp = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, mpStream)
            .With(e => e.ResolvedM3U8Url, mpUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, mpPage)
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
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, mpStream).Create(),
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, fbStream).Create()
            });

        var fbYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
                YieldThenWait(fb, mp, fbYielded, releaseMp.Task, ct));

        var playbackStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            .Returns((string url, string _, string _, Func<Task>? _, string? _, string? _, string? _,
                IReadOnlyDictionary<string, string>? _) =>
            {
                playbackStarted.TrySetResult(url);
                return playbackGate.Task;
            });

        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var startTask = sut.StartAsync(game, player.Object);

        // Act - /mp is still in flight (releaseMp not set) when first play opens
        var firstPlayedUrl = await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseMp.TrySetResult();
        var poolCount = 0;
        for (var i = 0; i < 50 && poolCount < 2; i++)
        {
            poolCount = switching.GetHealthyStreams().Count;
            if (poolCount < 2)
                await Task.Delay(20);
        }

        var pool = switching.GetHealthyStreams().Select(e => e.ResolvedM3U8Url).ToList();
        playbackGate.SetResult(PlaybackResult.SuccessResult("Playing"));
        var outcome = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - FB opened first; MP appended after it (stable channel numbers)
        Assert.Equal(fbUrl, firstPlayedUrl);
        Assert.Equal([fbUrl, mpUrl], pool);
        Assert.True(outcome.PlaybackResult?.Success);
        player.Verify(
            p => p.PlayVideoAsync(
                mpUrl,
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
    public async Task StartAsync_FasterLaterCandidate_PlaysFirst_FirstComeFirstServed()
    {
        // Arrange - rule 9 (Jon, 13 Nov 2026: "first come first served on playable
        // streams"): the later catalog row turns healthy first, so it plays first and
        // the earlier row never opens.
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
        Assert.Equal(laterUrl, switching.GetCurrentStream()?.ResolvedM3U8Url);
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
            Times.Once);
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
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_RelayedResolve_HoldsKeepAliveAcrossCandidates()
    {
        // Arrange
        var game = RemoteGame();
        var first = FailedStream("https://streams.example.com/match.html#north", "Channel North");
        var second = FailedStream("https://streams.example.com/match.html#south", "Channel South");
        var events = new List<string>();
        var keepAlive = _fixture.GetMock<IRemoteComputeKeepAlive>();
        keepAlive.Setup(k => k.Start()).Callback(() => events.Add("start"));
        keepAlive.Setup(k => k.Stop()).Callback(() => events.Add("stop"));
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(true);
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.LastRemoteComputeError)
            .Returns((string?)null);
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, first.Stream).Create(),
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, second.Stream).Create()
            });
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() =>
            {
                events.Add("resolve");
                return Yield(first, second);
            });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var outcome = await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        // Assert
        Assert.Equal(new[] { "start", "resolve", "stop" }, events);
        keepAlive.Verify(k => k.Start(), Times.Once);
        keepAlive.Verify(k => k.Stop(), Times.Once);
        Assert.False(outcome.LocalServiceUnavailable);
        Assert.True(outcome.NoWorkingStreams);
    }

    [Fact]
    public async Task StartAsync_WhenLanIsUp_DoesNotStartRelayKeepAlive()
    {
        // Arrange
        var game = RemoteGame();
        var stream = FailedStream("https://streams.example.com/match.html", "Channel North");
        var keepAlive = _fixture.GetMock<IRemoteComputeKeepAlive>();
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(true);
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream.Stream).Create()
            });
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => Yield(stream));
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        // Assert
        keepAlive.Verify(k => k.Start(), Times.Never);
        keepAlive.Verify(k => k.Stop(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_WhenPairedAndTheComputerIsBusy_DoesNotPutTheLanBannerBack()
    {
        var game = RemoteGame();
        var stream = FailedStream("https://streams.example.test/match.html", "Channel North");
        const string fault = "Local service failed: This computer is still finding a stream. Tap the game again to start a new search. Correlation id: abc";
        var statuses = new List<string>();
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(true);
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.LastRemoteComputeError)
            .Returns(fault);
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream.Stream).Create()
            });
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns(() => Yield(stream));
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.ProgressUpdated.Subscribe(new StatusCapture(statuses));

        var outcome = await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Contains(StreamResolutionOrchestrator.PairedFindingStatus, statuses);
        Assert.False(outcome.LocalServiceUnavailable);
        Assert.False(outcome.NoWorkingStreams);
        Assert.Equal(fault, outcome.RemoteComputeError);
    }

    private Game RemoteGame() =>
        _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();

    private EnrichedStream FailedStream(string pageUrl, string channel)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, pageUrl)
            .With(s => s.Channel, channel)
            .Create();
        return _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.Status, StreamResolutionStatus.Failed)
            .With(e => e.Referer, pageUrl)
            .Create();
    }

    [Fact]
    public async Task StartAsync_UserClosesPlayerWhileCandidatesStillTesting_ReturnsPromptlyAndStopsTesting()
    {
        // Regression: Back from the player left the homepage "Finding streams" modal up until the resolver
        // yielded another candidate, because the close was only noticed on the next yielded stream.
        var (game, enriched) = BuildGameAndHealthyStream();
        var resolverToken = default(CancellationToken);
        var resolverBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeCommon(game, enriched.Stream);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
            {
                resolverToken = ct;
                return YieldThenBlock(enriched, resolverBlocked, ct);
            });

        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangePlayer(playbackGate.Task);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var progress = new List<StreamResolutionProgress>();
        sut.ProgressUpdated.Subscribe(new ProgressCapture(progress));

        var startTask = sut.StartAsync(game, _fixture.GetMock<INativeVideoPlayerService>().Object);
        await resolverBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(startTask.IsCompleted); // still testing candidates while the stream plays

        playbackGate.SetResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        var outcome = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.UserClosed);
        Assert.True(resolverToken.IsCancellationRequested);
        AssertModalDismissed(progress);
    }

    [Theory]
    [InlineData(PlaybackCloseReasons.UserClosed)]
    [InlineData(PlaybackCloseReasons.PlayerClosed)]
    [InlineData("User closed video player")]
    public async Task StartAsync_ViewerLeavesAsResolverYieldsAnotherStream_DoesNotRelaunchAndDismissesModal(string closeMessage)
    {
        // The race behind the stuck modal: the player closes at the same moment the resolver yields another
        // healthy candidate. The viewer has left, so no second player may launch and IsResolving must end false.
        var (game, enriched) = BuildGameAndHealthyStream();
        var second = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _fixture.Build<StreamModel>()
                .With(s => s.Url, "https://streams.example.com/other.html")
                .With(s => s.Channel, "Channel South")
                .Create())
            .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/live/south.m3u8")
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, "https://streams.example.com/other.html")
            .Create();
        var firstYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeCommon(game, enriched.Stream);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
                YieldThenWait(enriched, second, firstYielded, releaseSecond.Task, ct));

        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangePlayer(playbackGate.Task);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var progress = new List<StreamResolutionProgress>();
        sut.ProgressUpdated.Subscribe(new ProgressCapture(progress));

        var startTask = sut.StartAsync(game, _fixture.GetMock<INativeVideoPlayerService>().Object);
        await firstYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        playbackGate.SetResult(PlaybackResult.Completed(closeMessage));
        releaseSecond.SetResult();
        var outcome = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.UserClosed);
        _fixture.GetMock<INativeVideoPlayerService>().Verify(
            p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
        AssertModalDismissed(progress);
    }

    [Fact]
    public async Task StartAsync_PlayerEndsWithoutUserClose_DoesNotCancelCandidateTesting()
    {
        var (game, enriched) = BuildGameAndHealthyStream();
        var resolverToken = default(CancellationToken);
        var firstYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResolver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeCommon(game, enriched.Stream);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
            {
                resolverToken = ct;
                return YieldThenWait(enriched, null, firstYielded, releaseResolver.Task, ct);
            });

        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangePlayer(playbackGate.Task);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        var startTask = sut.StartAsync(game, _fixture.GetMock<INativeVideoPlayerService>().Object);
        await firstYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        playbackGate.SetResult(PlaybackResult.Completed("Stream ended.", recoverable: false));
        releaseResolver.SetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The player finishing is not the viewer leaving: nothing may have cancelled candidate testing.
        Assert.False(resolverToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(PlaybackCloseReasons.UserClosed, true)]
    [InlineData(PlaybackCloseReasons.PlayerClosed, true)]
    [InlineData("User closed video player", true)]
    [InlineData("User closed MPV player", true)]
    [InlineData(PlaybackCloseReasons.PlayerRecreated, false)]   // a system recreate must not end candidate testing
    [InlineData("Stream ended.", false)]
    [InlineData("Playback error", false)]
    [InlineData("", false)]
    public async Task CancelEnumerationWhenViewerLeaves_CancelsOnlyWhenTheViewerLeft(string message, bool cancels)
    {
        // Deterministic: awaits the continuation itself instead of sleeping and hoping.
        using var cts = new CancellationTokenSource();
        var playback = Task.FromResult<PlaybackResult?>(PlaybackResult.Completed(message));

        await StreamResolutionOrchestrator.CancelEnumerationWhenViewerLeaves(playback, cts);

        Assert.Equal(cancels, cts.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelEnumerationWhenViewerLeaves_DoesNothingWhenPlaybackFaultsOrIsNull()
    {
        using var cts = new CancellationTokenSource();
        var faulted = Task.FromException<PlaybackResult?>(new InvalidOperationException("boom"));
        var nothing = Task.FromResult<PlaybackResult?>(null);

        await StreamResolutionOrchestrator.CancelEnumerationWhenViewerLeaves(faulted, cts);
        await StreamResolutionOrchestrator.CancelEnumerationWhenViewerLeaves(nothing, cts);

        Assert.False(cts.IsCancellationRequested);
    }

    private static void AssertModalDismissed(List<StreamResolutionProgress> progress)
    {
        Assert.NotEmpty(progress);
        Assert.False(progress[^1].IsResolving);
    }


    [Fact]
    public async Task Rule_NextOntoOneShotRow_DoesNotIssueBarePrefetchPlay()
    {
        // Arrange — the start row is cookie-bound (single-use), so first play takes a
        // fresh /play and warms the backup beside it; Next must ApplyWarmUnused
        // (no bare ResolveM3U8ForPlaybackAsync prefetch).
        const string warmUrl = "https://cdn.example.com/live/fresh.m3u8";
        var (game, first) = BuildGameAndHealthyStream();
        first.RequestHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://player.example.test/north",
            ["Cookie"] = "session=north",
        };
        var second = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _fixture.Build<StreamModel>()
                .With(s => s.Url, "https://streams.example.com/south.html")
                .With(s => s.Channel, "Channel South")
                .Create())
            .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/live/south.m3u8")
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, "https://streams.example.com/south.html")
            .With(e => e.RequestHeaders, new Dictionary<string, string> { ["Referer"] = "https://player.example.test/south" })
            .Create();
        var bothYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeCommon(game, first.Stream);
        var warm = new WarmPlaybackUrlCache();
        _fixture.Inject(warm);
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
                YieldSeedSiblingThenLater(first, second, second, bothYielded, Task.Delay(Timeout.Infinite, ct), ct));
        Func<Task>? onNext = null;
        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.GetMock<INativeVideoPlayerService>()
            .Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, string, Func<Task>?, string?, string?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, next, _, _, _, _) => onNext = next)
            .Returns(playbackGate.Task);
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response
            {
                Url = warmUrl,
                RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.example.test/" },
            });
        var startCount = 0;
        var prewarmer = new Mock<IUpcomingStreamPrewarmer>();
        prewarmer.Setup(p => p.Start(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref startCount))
            .Returns(Mock.Of<IDisposable>());
        prewarmer.Setup(p => p.Kick());
        _fixture.Inject(prewarmer.Object);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var statuses = new List<string>();
        sut.ProgressUpdated.Subscribe(new StatusCapture(statuses));

        // Act
        var startTask = sut.StartAsync(game, _fixture.GetMock<INativeVideoPlayerService>().Object);
        await bothYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && onNext == null; i++)
            await Task.Delay(20);
        Assert.NotNull(onNext);
        Assert.True(startCount >= 1, $"prewarmer was not wired (Start count={startCount})");
        for (var i = 0; i < 50 && warm.Peek(second) is null; i++)
            await Task.Delay(50);
        Assert.NotNull(warm.Peek(second));
        second.ResolvedM3U8Url = "https://cdn.example.com/live/south-find-burned.m3u8";
        second.UnusedResolvedUrl = false;
        second.RequestHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://player.example.test/south",
        };
        await onNext!();
        await Task.Delay(100);
        playbackGate.SetResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — ApplyWarmUnused; never bare ResolveM3U8ForPlaybackAsync prefetch.
        _fixture.GetMock<IApiService>().Verify(
            a => a.ResolveM3U8ForPlaybackAsync(It.IsAny<StreamModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.True(statuses.Contains("Switched to next stream"), string.Join(" | ", statuses));
        Assert.True(second.UnusedResolvedUrl, "Next must mark warm URL unused for AttachCached");
        Assert.Equal(warmUrl, second.ResolvedM3U8Url);
    }

    [Fact]
    public async Task Rule_NextOntoOneShotRow_NoWarm_ClearsAndKicks()
    {
        // Arrange — second joins after open-recommended (no partner warm); Next clears + Kick.
        var (game, first) = BuildGameAndHealthyStream();
        first.RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.example.test/north" };
        var second = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _fixture.Build<StreamModel>()
                .With(s => s.Url, "https://streams.example.com/south.html")
                .With(s => s.Channel, "Channel South")
                .Create())
            .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/live/south-find-burned.m3u8")
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, "https://streams.example.com/south.html")
            .With(e => e.RequestHeaders, new Dictionary<string, string> { ["Referer"] = "https://player.example.test/south" })
            .With(e => e.UnusedResolvedUrl, false)
            .Create();
        var firstYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(switching);
        _fixture.Inject(new WarmPlaybackUrlCache());
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, first.Stream).Create()
            });
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) =>
                YieldThenWait(first, second, firstYielded, releaseSecond.Task, ct));
        Func<Task>? onNext = null;
        var playbackGate = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.GetMock<INativeVideoPlayerService>()
            .Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, string, Func<Task>?, string?, string?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, next, _, _, _, _) => onNext = next)
            .Returns(playbackGate.Task);
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response
            {
                Url = "https://cdn.example.com/live/north-fresh.m3u8",
                RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.example.test/" },
            });
        var kickCount = 0;
        var prewarmer = new Mock<IUpcomingStreamPrewarmer>();
        prewarmer.Setup(p => p.Start(It.IsAny<CancellationToken>())).Returns(Mock.Of<IDisposable>());
        prewarmer.Setup(p => p.Kick()).Callback(() => Interlocked.Increment(ref kickCount));
        _fixture.Inject(prewarmer.Object);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var statuses = new List<string>();
        sut.ProgressUpdated.Subscribe(new StatusCapture(statuses));

        // Act
        var startTask = sut.StartAsync(game, _fixture.GetMock<INativeVideoPlayerService>().Object);
        await firstYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && onNext == null; i++)
            await Task.Delay(20);
        Assert.NotNull(onNext);
        releaseSecond.TrySetResult();
        for (var i = 0; i < 100 && !switching.GetHealthyStreams().Any(s => ReferenceEquals(s, second)); i++)
            await Task.Delay(20);
        Assert.Contains(second, switching.GetHealthyStreams());
        second.UnusedResolvedUrl = false;
        var kicksBeforeNext = kickCount;
        await onNext!();
        await Task.Delay(100);
        playbackGate.SetResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — ClearAndResolveFresh + Kick; never bare prefetch.
        _fixture.GetMock<IApiService>().Verify(
            a => a.ResolveM3U8ForPlaybackAsync(It.IsAny<StreamModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.True(statuses.Contains("Switched to next stream"), string.Join(" | ", statuses));
        Assert.True(
            kickCount > kicksBeforeNext,
            $"Next did not Kick prewarmer (before={kicksBeforeNext}, after={kickCount})");
        Assert.Null(second.ResolvedM3U8Url);
    }

    private (Game Game, EnrichedStream Enriched) BuildGameAndHealthyStream()
    {
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
            .With(s => s.Url, "https://streams.example.com/match.html")
            .With(s => s.Channel, "Channel North")
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, "https://cdn.example.com/live/north.m3u8")
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, stream.Url)
            .Create();
        return (game, enriched);
    }

    private void ArrangeCommon(Game game, StreamModel stream)
    {
        _fixture.Inject<IStreamSwitchingService>(_fixture.Create<StreamSwitchingService>());
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(new List<StreamSelectionCandidate>
            {
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()
            });
    }

    private void ArrangePlayer(Task<PlaybackResult> result)
        => _fixture.GetMock<INativeVideoPlayerService>()
            .Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(result);

    private static async IAsyncEnumerable<EnrichedStream> YieldSeedSiblingThenLater(
        EnrichedStream seed,
        EnrichedStream sibling,
        EnrichedStream later,
        TaskCompletionSource siblingsYielded,
        Task releaseLater,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return seed;
        yield return sibling;
        siblingsYielded.TrySetResult();
        await releaseLater.WaitAsync(ct);
        yield return later;
    }

    private static async IAsyncEnumerable<EnrichedStream> YieldThenBlock(
        EnrichedStream first,
        TaskCompletionSource blocked,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return first;
        blocked.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct); // models candidates still being health-tested
    }

    private static async IAsyncEnumerable<EnrichedStream> YieldThenWait(
        EnrichedStream first,
        EnrichedStream? second,
        TaskCompletionSource firstYielded,
        Task release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return first;
        firstYielded.TrySetResult();
        await release.WaitAsync(ct);
        if (second is not null)
            yield return second;
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

    private sealed class ProgressCapture(List<StreamResolutionProgress> progress) : IObserver<StreamResolutionProgress>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(StreamResolutionProgress value) => progress.Add(value);
    }

    private sealed class StatusCapture(List<string> statuses) : IObserver<StreamResolutionProgress>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(StreamResolutionProgress value) => statuses.Add(value.Status);
    }
}
