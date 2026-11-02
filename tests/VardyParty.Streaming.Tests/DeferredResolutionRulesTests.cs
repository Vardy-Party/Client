using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// Business rules: a row the local service never answered is Deferred, not
/// Failed. It is retried, re-queued, and never reported to crowd health. The
/// 16:35:57 UK-time cascade on 31 Oct 2026 dropped and reported such rows.
/// </summary>
public class DeferredResolutionRulesTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly Mock<ILocalLanPlayService> _localLanPlay;
    private readonly List<TimeSpan> _delays = [];

    public DeferredResolutionRulesTests()
    {
        _localLanPlay = _fixture.GetMock<ILocalLanPlayService>();
        _localLanPlay.SetupGet(s => s.UsesRemoteCompute).Returns(false);
        _localLanPlay.SetupGet(s => s.LastRemoteComputeError).Returns((string?)null);
        _localLanPlay.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _fixture.Inject<IDiscoveredChipNormalizer>(new V2PlaybackTransportPlugin());
    }

    private StreamResolver CreateResolver()
    {
        var sut = _fixture.Create<StreamResolver>();
        sut.DelayAsync = (d, _) =>
        {
            _delays.Add(d);
            return Task.CompletedTask;
        };
        return sut;
    }

    private StreamModel CreateStream(string url = "https://stream.example.test/1") =>
        _fixture.Build<StreamModel>().With(s => s.Url, url).With(s => s.Channel, "Channel North").Create();

    private static LocalServiceUnavailableException Unavailable() =>
        new(LocalServiceUnavailableReason.CallTimeout, "GET /play exceeded 20s");

    [Fact]
    public async Task Resolver_InfrastructureFailureEveryTime_IsDeferredNotFailed()
    {
        var stream = CreateStream();
        var calls = 0;
        _localLanPlay.Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ThrowsAsync(Unavailable());

        var results = await CollectAsync(CreateResolver().ResolveStreamsIncrementallyAsync([stream]));

        var row = Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Deferred, row.Status);
        Assert.Contains("CallTimeout", row.ErrorMessage);
        Assert.Equal(StreamResolver.InfrastructureRetryDelays.Length + 1, calls);
        Assert.Equal(StreamResolver.InfrastructureRetryDelays, _delays);
    }

    [Fact]
    public async Task Resolver_ServiceAnswersNoPlaylist_IsStillFailed()
    {
        var stream = CreateStream();
        _localLanPlay.Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);

        var results = await CollectAsync(CreateResolver().ResolveStreamsIncrementallyAsync([stream]));

        Assert.Equal(StreamResolutionStatus.Failed, Assert.Single(results).Status);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Resolver_InfrastructureBlipThenAnswer_UsesTheAnswer()
    {
        var stream = CreateStream();
        var calls = 0;
        _localLanPlay.Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1
                ? Task.FromException<M3U8Response?>(Unavailable())
                : Task.FromResult<M3U8Response?>(null));

        var results = await CollectAsync(CreateResolver().ResolveStreamsIncrementallyAsync([stream]));

        // The second answer is authoritative (here: no playlist, so Failed).
        Assert.Equal(StreamResolutionStatus.Failed, Assert.Single(results).Status);
        Assert.Equal(2, calls);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task Orchestrator_DeferredRow_IsRequeued_NotReported_AndOutcomeIsLocalServiceUnavailable()
    {
        var game = _fixture.Create<Game>();
        var stream = CreateStream();
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns([_fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()]);
        var resolves = 0;
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(It.IsAny<List<StreamModel>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns(() =>
            {
                resolves++;
                return Yield(new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Deferred, ErrorMessage = "Local service unavailable" });
            });
        var reports = new List<StreamHealthReport>();
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => reports.Add(r))
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.DelayAsync = (d, _) =>
        {
            _delays.Add(d);
            return Task.CompletedTask;
        };

        var outcome = await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Equal(1 + StreamResolutionOrchestrator.DeferredRequeueDelays.Length, resolves);
        Assert.Equal(StreamResolutionOrchestrator.DeferredRequeueDelays, _delays);
        Assert.Equal(1, outcome.DeferredStreamCount);
        Assert.True(outcome.LocalServiceUnavailable);
        Assert.False(outcome.NoWorkingStreams);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Orchestrator_DeferredRowRecoversOnRequeue_IsReportedWorkingAndPlayable()
    {
        var game = _fixture.Create<Game>();
        var stream = CreateStream();
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns([_fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()]);
        var switching = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(switching);
        var resolves = 0;
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(It.IsAny<List<StreamModel>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns(() => ++resolves == 1
                ? Yield(new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Deferred })
                : Yield(new EnrichedStream
                {
                    Stream = stream,
                    Status = StreamResolutionStatus.Healthy,
                    ResolvedM3U8Url = "https://cdn.example.test/live.m3u8",
                    Referer = stream.Url
                }));
        var reports = new List<StreamHealthReport>();
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => { lock (reports) reports.Add(r); })
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        sut.DelayAsync = (_, _) => Task.CompletedTask;

        var outcome = await sut.StartAsync(game, _fixture.GetMock<IPlaybackLauncher>().Object);

        Assert.Equal(2, resolves);
        Assert.Equal(0, outcome.DeferredStreamCount);
        Assert.False(outcome.LocalServiceUnavailable);
        StreamHealthReport[] snapshot;
        lock (reports) snapshot = reports.ToArray();
        Assert.Contains(snapshot, r => r.Status == "working" && r.StreamUrl == stream.Url);
        Assert.DoesNotContain(snapshot, r => r.Status is "failed" or "unknown");
    }

    private static async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var s in streams)
        {
            await Task.Yield();
            yield return s;
        }
    }

    private static async Task<List<EnrichedStream>> CollectAsync(IAsyncEnumerable<EnrichedStream> source)
    {
        var list = new List<EnrichedStream>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}