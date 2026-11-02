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
/// Startup race: two fresh /play calls, a video check before a URL can win, and an
/// empty recommended row must not hold the finding-streams dialog when a cached row is ready.
/// </summary>
public class StartupRaceBusinessRulesTests
{
    private const string PageUrl = "https://streams.example.com/match.html";
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly StreamSwitchingService _switching;
    private readonly WarmPlaybackUrlCache _warm = new();
    private readonly Game _game;

    public StartupRaceBusinessRulesTests()
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
    public async Task Rule_EmptyRecommended_PlaysCachedRowWhilePartnerPlayIsStillRunning()
    {
        // Arrange
        var first = Soak("north");
        var partner = Soak("south");
        var cached = Cached("east", "https://cdn.example.com/live/east.m3u8");
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
        Assert.DoesNotContain(first, _switching.GetHealthyStreams());
        Assert.Contains(cached, _switching.GetHealthyStreams());
        releasePartner.TrySetResult(null);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_AudioOnlyRecommended_LosesToPartnerWithVideo()
    {
        // Arrange
        var first = Soak("north");
        var partner = Soak("south");
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
        _fixture.GetMock<IStreamHealthService>().Verify(h => h.ReportHealthAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<StreamHealthReport>(r => r.StreamUrl == first.Stream.Url
                && r.Status == "failed"
                && r.Error == "No video (audio only)"),
            It.IsAny<CancellationToken>()), Times.Once);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rule_ShortLivedWinnerToken_PlaysAppliedUrl_WithoutSecondPlay()
    {
        // Arrange — readable expiry under MinComfortableLife so LoserTakeBy(PutUntil)
        // would no-op; winner must Apply WinnerValue and play once, not re-/play.
        var first = Soak("north");
        var partner = Soak("south");
        Candidates(first, partner);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(50);
        var winnerUrl =
            $"https://cdn.example.com/live/north.m3u8?e={expiry.ToUnixTimeSeconds()}";
        var partnerUrl =
            $"https://cdn.example.com/live/south.m3u8?e={expiry.ToUnixTimeSeconds()}";
        FreshPlay(first, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = winnerUrl,
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = PageUrl },
        }));
        FreshPlay(partner, () => Task.FromResult<M3U8Response?>(new M3U8Response
        {
            Url = partnerUrl,
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
        Assert.Equal(winnerUrl, first.ResolvedM3U8Url);
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

    private EnrichedStream Soak(string name)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, $"Channel {name}")
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

    private EnrichedStream Cached(string name, string url)
    {
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, $"{PageUrl}#{name}")
            .With(s => s.Channel, $"Channel {name}")
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
