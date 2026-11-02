using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.V2;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules (31 Oct 2026): the first start''s fresh <c>/play</c> follows
/// the find phase. A local-service blip is retried with
/// <see cref="InfrastructureRetryPolicy.FreshResolveDelays"/>; when it never
/// answers, the start is deferred ("Local service unavailable"), the row stays in
/// the pool and nothing is reported "failed". Only a service answer with no
/// playlist fails the start.
/// </summary>
public class FreshResolveInfrastructureRulesTests
{
    private const string PageUrl = "https://streams.example.test/match.html";
    private const string FreshUrl = "https://cdn.example.test/live/fresh.m3u8?token=new";

    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly List<TimeSpan> _delays = [];
    private readonly List<StreamHealthReport> _reports = [];
    private readonly StreamSwitchingService _switching;
    private readonly StreamModel _stream;
    private readonly Mock<INativeVideoPlayerService> _player;
    private int _resolves;

    public FreshResolveInfrastructureRulesTests()
    {
        var lan = _fixture.GetMock<ILocalLanPlayService>();
        lan.Setup(x => x.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        lan.SetupGet(x => x.UsesRemoteCompute).Returns(false);
        lan.SetupGet(x => x.LastRemoteComputeError).Returns((string?)null);
        _fixture.Inject<IDiscoveredChipNormalizer>(new V2PlaybackTransportPlugin());

        _stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, PageUrl)
            .With(s => s.Channel, "Channel North")
            .Create();
        _switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(_switching);
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns([_fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, _stream).Create()]);
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => { lock (_reports) _reports.Add(r); })
            .Returns(Task.CompletedTask);
        _player = _fixture.GetMock<INativeVideoPlayerService>();
        _player.Setup(p => p.PlayVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.SuccessResult("Playing"));
    }

    private void PoolRow(string? cachedUrl)
    {
        var row = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _stream)
            .With(e => e.ResolvedM3U8Url, cachedUrl)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Referer, PageUrl)
            .Without(e => e.RequestHeaders)
            .Without(e => e.RewrittenSegments)
            .Create();
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(It.IsAny<List<StreamModel>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns(() => Yield(row));
    }

    private void FreshResolve(params Func<M3U8Response?>[] answers)
    {
        _fixture.GetMock<IApiService>()
            .Setup(a => a.GetM3U8UrlAsync(PageUrl, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var i = Math.Min(Interlocked.Increment(ref _resolves) - 1, answers.Length - 1);
                try
                {
                    return Task.FromResult(answers[i]());
                }
                catch (Exception ex)
                {
                    return Task.FromException<M3U8Response?>(ex);
                }
            });
    }

    private static M3U8Response? Blip() =>
        throw new LocalServiceUnavailableException(LocalServiceUnavailableReason.CallTimeout, "GET /play exceeded 20s");

    private static M3U8Response? Disabled() =>
        throw new LocalServiceUnavailableException(LocalServiceUnavailableReason.MpDisabled, "V2/MP scrape disabled on the local service");

    private static M3U8Response? Fresh() => new() { Url = FreshUrl };

    private StreamResolutionOrchestrator CreateSut()
    {
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.DelayAsync = (d, _) =>
        {
            lock (_delays) _delays.Add(d);
            return Task.CompletedTask;
        };
        return sut;
    }

    private Times PlayedWith(string url, Times times)
    {
        _player.Verify(p => p.PlayVideoAsync(url, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), times);
        return times;
    }

    private void AssertNothingReportedFailed()
    {
        lock (_reports)
            Assert.DoesNotContain(_reports, r => r.Status is "failed" or "declined");
    }

    [Fact]
    public async Task FirstStart_FreshResolveBlipThenAnswer_RetriesSameStreamAndPlays()
    {
        PoolRow(cachedUrl: null);
        FreshResolve(Blip, Fresh);

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.True(outcome.PlaybackResult?.Success);
        Assert.False(outcome.LocalServiceUnavailable);
        Assert.Equal(2, _resolves);
        Assert.Contains(InfrastructureRetryPolicy.FreshResolveDelays[0], _delays);
        PlayedWith(FreshUrl, Times.Once());
        AssertNothingReportedFailed();
    }

    [Fact]
    public async Task FirstStart_LocalServiceNeverAnswers_DefersStart_KeepsRow_ReportsNothing()
    {
        PoolRow(cachedUrl: null);
        FreshResolve(Blip);

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.Equal(1 + InfrastructureRetryPolicy.FreshResolveDelays.Length, _resolves);
        foreach (var d in InfrastructureRetryPolicy.FreshResolveDelays)
            Assert.Contains(d, _delays);
        Assert.True(outcome.LocalServiceUnavailable);
        Assert.False(outcome.NoWorkingStreams);
        Assert.True(outcome.PlaybackResult?.IsLocalServiceUnavailable);
        Assert.Single(_switching.GetHealthyStreams());
        PlayedWith(FreshUrl, Times.Never());
        AssertNothingReportedFailed();
    }

    [Fact]
    public async Task FirstStart_MpDisabled_DefersAtOnceWithoutRetries()
    {
        PoolRow(cachedUrl: null);
        FreshResolve(Disabled);

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.Equal(1, _resolves);
        Assert.DoesNotContain(InfrastructureRetryPolicy.FreshResolveDelays[0], _delays);
        Assert.True(outcome.LocalServiceUnavailable);
        Assert.Single(_switching.GetHealthyStreams());
        AssertNothingReportedFailed();
    }

    [Fact]
    public async Task FirstStart_ServiceAnswersNoPlaylist_IsStillAFailedStart()
    {
        PoolRow(cachedUrl: null);
        FreshResolve(() => null);

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.Equal(1, _resolves);
        Assert.False(outcome.PlaybackResult?.IsLocalServiceUnavailable ?? false);
        Assert.False(outcome.LocalServiceUnavailable);
        lock (_reports)
            Assert.Contains(_reports, r => r.Status == "failed" && r.StreamUrl == PageUrl);
    }

    [Fact]
    public async Task CachedUrlFails_FreshRetryHitsLocalServiceDown_DefersAndReportsNothing()
    {
        const string cached = "https://cdn.example.test/live/cached.m3u8?token=old";
        PoolRow(cached);
        FreshResolve(Blip);
        _player.Setup(p => p.PlayVideoAsync(cached, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.Completed("CDN token expired", recoverable: true));

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.Equal(1 + InfrastructureRetryPolicy.FreshResolveDelays.Length, _resolves);
        Assert.True(outcome.LocalServiceUnavailable);
        Assert.Single(_switching.GetHealthyStreams());
        AssertNothingReportedFailed();
    }

    [Fact]
    public async Task PlayerClosedWithLocalServiceUnavailable_IsNotReportedOrDropped()
    {
        PoolRow("https://cdn.example.test/live/cached.m3u8?token=ok");
        FreshResolve(Fresh);
        _player.Setup(p => p.PlayVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(PlaybackResult.Completed($"{PlaybackCloseReasons.LocalServiceUnavailable}: GET /play exceeded 20s", true));

        var outcome = await CreateSut().StartAsync(_fixture.Create<Game>(), _player.Object);

        Assert.True(outcome.LocalServiceUnavailable);
        Assert.Single(_switching.GetHealthyStreams());
        Assert.Equal(0, _resolves); // no cached-URL fresh retry on an infrastructure close
        AssertNothingReportedFailed();
    }

    [Fact]
    public async Task Resolver_MpDisabled_DefersWithoutRetrying()
    {
        var lan = _fixture.GetMock<ILocalLanPlayService>();
        var calls = 0;
        lan.Setup(s => s.ResolveM3U8UrlAsync(PageUrl, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ThrowsAsync(new LocalServiceUnavailableException(LocalServiceUnavailableReason.MpDisabled, "V2/MP scrape disabled"));
        var resolver = _fixture.Create<StreamResolver>();
        resolver.DelayAsync = (d, _) =>
        {
            _delays.Add(d);
            return Task.CompletedTask;
        };

        var rows = new List<EnrichedStream>();
        await foreach (var row in resolver.ResolveStreamsIncrementallyAsync([_stream]))
            rows.Add(row);

        var only = Assert.Single(rows);
        Assert.Equal(StreamResolutionStatus.Deferred, only.Status);
        Assert.Equal(1, calls);
        Assert.Empty(_delays);
    }

    [Fact]
    public void Policy_RetriesOnlyRetryableFailures_ForTheConfiguredAttempts()
    {
        var blip = new LocalServiceUnavailableException(LocalServiceUnavailableReason.ConnectionFailed, "refused");
        var auth = new LocalServiceUnavailableException(LocalServiceUnavailableReason.Unauthorized, "401");

        Assert.True(InfrastructureRetryPolicy.ShouldRetryFreshResolve(blip, 0));
        Assert.True(InfrastructureRetryPolicy.ShouldRetryFreshResolve(blip, InfrastructureRetryPolicy.FreshResolveDelays.Length - 1));
        Assert.False(InfrastructureRetryPolicy.ShouldRetryFreshResolve(blip, InfrastructureRetryPolicy.FreshResolveDelays.Length));
        Assert.False(InfrastructureRetryPolicy.ShouldRetryFreshResolve(auth, 0));
    }

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var s in streams)
        {
            await Task.Yield();
            yield return s;
        }
    }
}