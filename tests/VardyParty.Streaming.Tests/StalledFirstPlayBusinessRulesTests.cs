using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules for a first play that fails while candidates are still being
/// tested (docs/STREAM_PLAYBACK_RULES.md "Stalled first play"). The "Finding
/// streams... 2 healthy" dialog sat until the whole sweep ended because the
/// orchestrator only looked at the first play's result after enumeration.
/// </summary>
public class StalledFirstPlayBusinessRulesTests
{
    private const string PageUrl = "https://streams.example.com/match.html";
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly StreamSwitchingService _switching;
    private readonly Game _game;

    public StalledFirstPlayBusinessRulesTests()
    {
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(false);
        // Freeze before the orchestrator is built so it gets these instances.
        _fixture.GetMock<IStreamHealthService>();
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);
        _switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(_switching);
        _game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .With(g => g.BBCLeague, string.Empty)
            .Create();
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.InitializeAsync(_game, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Rule_EmptyFirstPlay_PlaysNextHealthyRowWhileTestingContinues()
    {
        // Row 1's fresh /play comes back empty; row 2 is healthy with a cached URL.
        // Candidate testing is still running (the resolver never finishes).
        var first = Row("north", resolvedUrl: null);
        var second = Row("south", "https://cdn.example.com/live/south.m3u8");
        Candidates(first, second);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ResolverYields(ct => YieldThenBlock([first, second], blocked, ct));
        FreshPlay(first, () => Task.FromResult<M3U8Response?>(null));

        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });

        using var cts = new CancellationTokenSource();
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var start = sut.StartAsync(_game, player.Object, cts.Token);

        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(second.ResolvedM3U8Url, url);
        Assert.False(blocked.Task.IsCompleted && start.IsCompleted);
        VerifyReported(first, "failed", Times.Once());
        Assert.DoesNotContain(first, _switching.GetHealthyStreams());
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_FirstPlayFailsWithEmptyPool_NextRowToJoinStartsPlayback()
    {
        var first = Row("north", resolvedUrl: null);
        var second = Row("south", "https://cdn.example.com/live/south.m3u8");
        Candidates(first, second);
        var firstFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ResolverYields(ct => YieldWaitYield(first, firstFailed.Task, second, ct));
        FreshPlay(first, () =>
        {
            firstFailed.TrySetResult();
            return Task.FromResult<M3U8Response?>(null);
        });

        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.SuccessResult("Playing"));
        });

        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var outcome = await sut.StartAsync(_game, player.Object).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(second.ResolvedM3U8Url, await played.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(outcome.PlaybackResult?.Success);
        VerifyReported(first, "failed", Times.Once());
    }

    [Fact]
    public async Task Rule_FirstPlayLocalServiceUnavailable_NoReportNoRemovalNoAdvance()
    {
        var first = Row("north", resolvedUrl: null);
        var second = Row("south", "https://cdn.example.com/live/south.m3u8");
        Candidates(first, second);
        ResolverYields(_ => Yield(first, second));
        FreshPlay(first, () => throw new InfrastructureUnavailableException("connection refused"));
        var player = Player((_, _) => Task.FromResult(PlaybackResult.SuccessResult("Playing")));

        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.DelayAsync = (_, _) => Task.CompletedTask;
        var outcome = await sut.StartAsync(_game, player.Object).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.LocalServiceUnavailable);
        VerifyReported(first, "failed", Times.Never());
        Assert.Contains(first, _switching.GetHealthyStreams());
        player.Verify(p => p.PlayVideoAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Never);
    }

    [Fact]
    public async Task Rule_FailedStartPlaysTheRowThatTookItsIndex_AndKeepsThatResult()
    {
        // Three rows. The player opens row 1 and its session ends without playing.
        // The old TryNextHealthyStreamAsync played row 3 (GetNextHealthyStream after
        // the removal had already moved the index to row 2) and dropped its result.
        var first = Row("north", "https://cdn.example.com/live/north.m3u8");
        var second = Row("south", "https://cdn.example.com/live/south.m3u8");
        var third = Row("east", "https://cdn.example.com/live/east.m3u8");
        Candidates(first, second, third);
        ResolverYields(_ => Yield(first, second, third));

        const string northUrl = "https://cdn.example.com/live/north.m3u8";
        var urls = new List<string>();
        var player = Player((url, _) =>
        {
            lock (urls)
                urls.Add(url);
            return Task.FromResult(url == northUrl
                ? PlaybackResult.Completed("Playback error: never established", recoverable: true)
                : PlaybackResult.SuccessResult("Playing"));
        });

        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var outcome = await sut.StartAsync(_game, player.Object).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([northUrl, "https://cdn.example.com/live/south.m3u8"], urls);
        Assert.True(outcome.PlaybackResult?.Success);
        Assert.Same(second, _switching.GetCurrentStream());
        VerifyReported(first, "failed", Times.Once());
        VerifyReported(second, "failed", Times.Never());
    }

    private EnrichedStream Row(string name, string? resolvedUrl)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, $"Channel {name}")
            .Create();
        return _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, resolvedUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, PageUrl)
            .Without(e => e.RequestHeaders)
            .Without(e => e.RewrittenSegments)
            .Create();
    }

    private void Candidates(params EnrichedStream[] rows) =>
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(rows.Select(r => _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, r.Stream).Create()).ToList());

    private void ResolverYields(Func<CancellationToken, IAsyncEnumerable<EnrichedStream>> yields) =>
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(
                It.IsAny<List<StreamModel>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<int>?>()))
            .Returns((List<StreamModel> _, int _, CancellationToken ct, Action<int>? _) => yields(ct));

    private void FreshPlay(EnrichedStream row, Func<Task<M3U8Response?>> response) =>
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(
                row.Stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(response);

    private Mock<INativeVideoPlayerService> Player(Func<string, string, Task<PlaybackResult>> play)
    {
        var player = _fixture.GetMock<INativeVideoPlayerService>();
        player.Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns((string url, string referer, string _, Func<Task>? _, string? _, string? _, string? _,
                IReadOnlyDictionary<string, string>? _) => play(url, referer));
        return player;
    }

    private void VerifyReported(EnrichedStream row, string status, Times times) =>
        _fixture.GetMock<IStreamHealthService>()
            .Verify(h => h.ReportHealthAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<StreamHealthReport>(r => r.StreamUrl == row.Stream.Url && r.Status == status),
                It.IsAny<CancellationToken>()), times);

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var stream in streams)
            yield return stream;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<EnrichedStream> YieldThenBlock(
        EnrichedStream[] streams,
        TaskCompletionSource blocked,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var stream in streams)
            yield return stream;
        blocked.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }

    private static async IAsyncEnumerable<EnrichedStream> YieldWaitYield(
        EnrichedStream first,
        Task release,
        EnrichedStream second,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return first;
        await release.WaitAsync(ct);
        // Let the chain record "pool empty" before the next row joins.
        await Task.Delay(100, ct);
        yield return second;
    }
}
