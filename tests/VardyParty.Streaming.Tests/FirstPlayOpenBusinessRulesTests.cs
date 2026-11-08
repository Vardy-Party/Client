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
/// Real-life business rules for <see cref="FirstPlayOpenStrategy"/> through the
/// orchestrator (all platforms). Scenarios mirror Bravia/Windows stalls:
/// two healthy → open one /play; partner warms for Next; MP harvest reuse.
/// </summary>
public class FirstPlayOpenBusinessRulesTests
{
    private const string PageUrl = "https://streams.example.com/match.html";
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly StreamSwitchingService _switching;
    private readonly WarmPlaybackUrlCache _warm = new();
    private readonly ResolverRaceThrottle _throttle = new();
    private readonly Game _game;

    public FirstPlayOpenBusinessRulesTests()
    {
        _fixture.GetMock<ILocalLanPlayService>()
            .Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _fixture.GetMock<ILocalLanPlayService>()
            .SetupGet(x => x.UsesRemoteCompute)
            .Returns(false);
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
        _fixture.Inject(_warm);
        _fixture.Inject(_throttle);
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
    public async Task Rule_TwoHealthyFb_OpensRecommendedWithoutWaitingOnSlowPartner()
    {
        // Arrange — 2 healthy → finding UI must not wait ~25s on dual /play race.
        var recommended = Soak("Channel North");
        var partner = Soak("Channel East");
        Candidates(recommended, partner);

        var partnerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePartner = new TaskCompletionSource<M3U8Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recUrl = "https://hls.example.com/channel-north.m3u8";
        FreshPlay(recommended, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = recUrl,
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        FreshPlay(partner, async () =>
        {
            partnerStarted.TrySetResult();
            return await releasePartner.Task;
        });
        _fixture.Inject<IRacePlaylistProbe>(new StubProbe(_ => PlaylistVideoVerdict.Video));
        ResolverYields(_ => Yield(recommended, partner));

        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.PartnerLookoutAsync = ct => Task.Delay(50, ct);

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(recUrl, url);
        Assert.True(partnerStarted.Task.IsCompleted, "partner warm should have started on second slot");
        Assert.False(releasePartner.Task.IsCompleted, "picture must not wait on partner /play");
        _fixture.GetMock<IApiService>().Verify(
            a => a.GetM3U8UrlAsync(
                recommended.Stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        releasePartner.TrySetResult(new M3U8Response
        {
            Url = "https://edge.example.com/channel-east.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        });
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_TwoHealthyFb_PartnerWarmsIntoCacheForSeamlessNext()
    {
        // Arrange
        var recommended = Soak("Channel North");
        var partner = Soak("Channel East");
        Candidates(recommended, partner);
        var partnerUrl = "https://edge2.example.com/channel-east.m3u8";
        FreshPlay(recommended, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = "https://hls.example.com/channel-north.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        FreshPlay(partner, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = partnerUrl,
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://streams.example.com/" },
        }));
        _fixture.Inject<IRacePlaylistProbe>(new StubProbe(_ => PlaylistVideoVerdict.Video));
        ResolverYields(_ => Yield(recommended, partner));

        Func<Task>? onNext = null;
        var holdPlayback = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, next) =>
        {
            onNext = next;
            firstUrl.TrySetResult(url);
            return holdPlayback.Task;
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.PartnerLookoutAsync = ct => Task.Delay(50, ct);

        // Act
        var start = sut.StartAsync(_game, player.Object);
        await firstUrl.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — partner warm finishes into PutUntil while first stream plays.
        for (var i = 0; i < 50 && _warm.Peek(partner) is null; i++)
            await Task.Delay(50);
        Assert.NotNull(_warm.Peek(partner));
        Assert.Equal(partnerUrl, _warm.Peek(partner)!.Response.Url);

        var now = DateTimeOffset.UtcNow;
        var warmReady = _warm.Peek(partner) is { } peek
            && peek.Expiry - now > PlaybackUrlReusePolicy.MinComfortableLife;
        Assert.True(warmReady);
        Assert.Equal(
            FirstPlayOpenStrategy.NextPrepareKind.ApplyWarmUnused,
            FirstPlayOpenStrategy.PlanNextPrepare(partner, now, warmReady));

        Assert.NotNull(onNext);
        partner.ResolvedM3U8Url = "https://cdn.example.com/burned.m3u8";
        partner.UnusedResolvedUrl = false;
        await onNext!();
        Assert.True(partner.UnusedResolvedUrl, "Next must mark warm URL unused for AttachCached");
        Assert.Equal(partnerUrl, partner.ResolvedM3U8Url);

        holdPlayback.SetResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_MpHarvestProbeReady_OpensWithoutFreshPlay()
    {
        // Arrange — /mp streamed playlist + rewritten segments; token still has MinComfortableLife
        // vs orchestrator UtcNow (frozen fixture UzhiUrl is already past wall-clock).
        var harvestUrl =
            $"https://cdn.example.com/mp-harvest.m3u8?e={DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}";
        var mp = MpHealthy("Channel MP Alpha", harvestUrl);
        Candidates(mp);
        ResolverYields(_ => Yield(mp));
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(harvestUrl, url);
        _fixture.GetMock<IApiService>().Verify(
            a => a.GetM3U8UrlAsync(
                mp.Stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_EmptyRecommended_PlaysCachedThird_WhilePartnerStillRunning()
    {
        // Arrange
        var first = Soak("Channel North");
        var partner = Soak("Channel South");
        var cached = Cached("Channel East", "https://cdn.example.com/live/east.m3u8");
        Candidates(first, partner, cached);
        var partnerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePartner = new TaskCompletionSource<M3U8Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FreshPlay(first, () => Task.FromResult<M3U8Response?>(null));
        FreshPlay(partner, async () =>
        {
            partnerStarted.TrySetResult();
            return await releasePartner.Task;
        });
        ResolverYields(_ => Yield(first, partner, cached));
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.PartnerLookoutAsync = ct => Task.Delay(50, ct);

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(cached.ResolvedM3U8Url, url);
        Assert.True(partnerStarted.Task.IsCompleted);
        Assert.False(releasePartner.Task.IsCompleted);
        releasePartner.TrySetResult(null);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_AudioOnlyRecommended_FallsBackToPartnerVideo()
    {
        // Arrange
        var first = Soak("Channel North");
        var partner = Soak("Channel South");
        Candidates(first, partner);
        FreshPlay(first, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = "https://cdn.example.com/live/north.m3u8",
        }));
        FreshPlay(partner, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = "https://cdn.example.com/live/south.m3u8",
        }));
        _fixture.Inject<IRacePlaylistProbe>(new StubProbe(url =>
            url.Contains("north", StringComparison.Ordinal)
                ? PlaylistVideoVerdict.AudioOnly
                : PlaylistVideoVerdict.Video));
        ResolverYields(_ => Yield(first, partner));
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.PartnerLookoutAsync = ct => Task.Delay(50, ct);

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal("https://cdn.example.com/live/south.m3u8", url);
        Assert.DoesNotContain(first, _switching.GetHealthyStreams());
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_SoloFb_OpensWithSingleFreshPlay()
    {
        // Arrange
        var only = Soak("Channel West");
        Candidates(only);
        var onlyUrl = "https://edge.example.com/channel-west.m3u8";
        FreshPlay(only, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = onlyUrl,
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        _fixture.Inject<IRacePlaylistProbe>(new StubProbe(_ => PlaylistVideoVerdict.Unverified));
        ResolverYields(_ => Yield(only));
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(onlyUrl, url);
        _fixture.GetMock<IApiService>().Verify(
            a => a.GetM3U8UrlAsync(
                only.Stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_PlaybackPriorityHold_BlocksFindPhaseEnter_UntilReleased()
    {
        // Arrange — find-phase EnterAsync waits while priority/race hold is up.
        using var hold = _throttle.BeginPlaybackPriority();
        Assert.True(_throttle.RaceInFlight);

        var entered = false;
        var waiting = _throttle.EnterAsync(CancellationToken.None).ContinueWith(
            t =>
            {
                entered = true;
                t.Result.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Act
        await Task.Delay(80);
        Assert.False(entered);
        hold.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.True(entered);
        Assert.False(_throttle.RaceInFlight);
    }

    [Fact]
    public async Task Rule_ShortLivedOpenRecommended_PlaysAppliedUrl_WithoutSecondPlay()
    {
        // Arrange
        var first = Soak("Channel North");
        var partner = Soak("Channel South");
        Candidates(first, partner);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(50);
        var winnerUrl = $"https://cdn.example.com/live/north.m3u8?e={expiry.ToUnixTimeSeconds()}";
        FreshPlay(first, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = winnerUrl,
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        FreshPlay(partner, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = $"https://cdn.example.com/live/south.m3u8?e={expiry.ToUnixTimeSeconds()}",
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        _fixture.Inject<IRacePlaylistProbe>(new StubProbe(_ => PlaylistVideoVerdict.Video));
        ResolverYields(_ => Yield(first, partner));
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = Player((url, _) =>
        {
            played.TrySetResult(url);
            return Task.FromResult(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        });
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.PartnerLookoutAsync = ct => Task.Delay(50, ct);

        // Act
        var start = sut.StartAsync(_game, player.Object);
        var url = await played.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(winnerUrl, url);
        _fixture.GetMock<IApiService>().Verify(
            a => a.GetM3U8UrlAsync(
                first.Stream.Url,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // --- helpers -------------------------------------------------------------

    private EnrichedStream Soak(string name)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, name)
            .Create();
        return _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, (string?)null)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, PageUrl)
            .With(e => e.RequestHeaders, new Dictionary<string, string> { ["Referer"] = PageUrl })
            .Without(e => e.RewrittenSegments)
            .Create();
    }

    private EnrichedStream MpHealthy(string name, string url)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, name)
            .Create();
        return _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, url)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.UnusedResolvedUrl, false)
            .With(e => e.RewrittenSegments, new List<string> { "https://cdn.example.com/seg.exe?_s2=1" })
            .Without(e => e.RequestHeaders)
            .Create();
    }

    private EnrichedStream Cached(string name, string url)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, name)
            .Create();
        return _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, url)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, PageUrl)
            .Without(e => e.RequestHeaders)
            .Without(e => e.RewrittenSegments)
            .Create();
    }

    private void Candidates(params EnrichedStream[] rows) =>
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns(rows.Select(r =>
                _fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, r.Stream).Create()).ToList());

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

    private Mock<INativeVideoPlayerService> Player(Func<string, Func<Task>?, Task<PlaybackResult>> play)
    {
        var player = _fixture.GetMock<INativeVideoPlayerService>();
        player.Setup(p => p.PlayVideoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns((string url, string _, string _, Func<Task>? next, string? _, string? _, string? _,
                IReadOnlyDictionary<string, string>? _) => play(url, next));
        return player;
    }

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var stream in streams)
            yield return stream;
        await Task.CompletedTask;
    }

    private sealed class StubProbe(Func<string, PlaylistVideoVerdict> classify) : IRacePlaylistProbe
    {
        public Task<PlaylistVideoVerdict> ClassifyAsync(M3U8Response response, CancellationToken cancellationToken)
            => Task.FromResult(classify(response.Url));
    }
}
