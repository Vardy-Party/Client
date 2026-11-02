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
/// Business rules: when the player gives up on its last pool row(s) as CDN
/// unreachable (<see cref="PlaybackCloseReasons.CdnUnreachable"/>), the
/// orchestrator puts every row still in the pool on the background recheck
/// (2 min, 5 min, then every 10 min while the game is live). A row that comes
/// back Healthy restarts playback on the same pool row; a recheck that proves
/// the row dead reports and drops it; a row still unreachable is never reported
/// and never dropped. Other player failures (audio only, decoys) are untouched.
/// </summary>
public class CdnUnreachablePoolRecheckTests
{
    private const string Manifest = "https://hls.lshls.st/live/index.m3u8";
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private sealed record Harness(
        StreamResolutionOrchestrator Sut,
        StreamSwitchingService Pool,
        Mock<IPlaybackLauncher> Launcher,
        List<StreamHealthReport> Reports,
        List<TimeSpan> RecheckDelays);

    private Harness Build(StreamModel stream, Func<int, EnrichedStream> resolveCall, Queue<PlaybackResult> plays,
        Func<DateTime>? clock = null, Action<TimeSpan>? onRecheckDelay = null)
    {
        var lan = _fixture.GetMock<ILocalLanPlayService>();
        lan.SetupGet(s => s.UsesRemoteCompute).Returns(false);
        lan.SetupGet(s => s.LastRemoteComputeError).Returns((string?)null);
        lan.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _fixture.Inject<IDiscoveredChipNormalizer>(new V2PlaybackTransportPlugin());
        _fixture.GetMock<IStreamSelectionCoordinator>()
            .Setup(c => c.GetOrderedCandidates())
            .Returns([_fixture.Build<StreamSelectionCandidate>().With(c => c.Stream, stream).Create()]);
        var pool = _fixture.Create<StreamSwitchingService>();
        _fixture.Inject<IStreamSwitchingService>(pool);
        var resolves = 0;
        _fixture.GetMock<IStreamResolver>()
            .Setup(r => r.ResolveStreamsIncrementallyAsync(It.IsAny<List<StreamModel>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<int>?>()))
            .Returns(() => Yield(resolveCall(++resolves)));
        var reports = new List<StreamHealthReport>();
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => { lock (reports) reports.Add(r); })
            .Returns(Task.CompletedTask);
        var launcher = _fixture.GetMock<IPlaybackLauncher>();
        launcher.Setup(p => p.PlayVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(() => plays.Count > 0 ? plays.Dequeue() : PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));
        var sut = _fixture.Create<StreamResolutionOrchestrator>();
        var recheckDelays = new List<TimeSpan>();
        sut.DelayAsync = (_, _) => Task.CompletedTask;
        sut.CdnRecheckDelayAsync = (d, _) =>
        {
            lock (recheckDelays) recheckDelays.Add(d);
            onRecheckDelay?.Invoke(d);
            return Task.CompletedTask;
        };
        if (clock != null)
            sut.UtcNow = clock;
        return new Harness(sut, pool, launcher, reports, recheckDelays);
    }

    private StreamModel CreateStream() =>
        _fixture.Build<StreamModel>().With(s => s.Url, "https://streams.example.test/1").With(s => s.Channel, "lshls 1").Create();

    private static EnrichedStream Healthy(StreamModel stream) => new()
    {
        Stream = stream,
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = Manifest,
        Referer = stream.Url
    };

    private static EnrichedStream CdnDeferred(StreamModel stream) => new()
    {
        Stream = stream,
        Status = StreamResolutionStatus.Deferred,
        DeferReason = StreamDeferReason.CdnUnreachable,
        ErrorMessage = $"{CdnReachabilityPolicy.RecheckingMessage} (CdnUnreachable)"
    };

    private static PlaybackResult GaveUpCdnUnreachable() =>
        PlaybackResult.Completed($"{PlaybackCloseReasons.CdnUnreachable}: ConnectException: Failed to connect to hls.lshls.st");

    private Game LiveGame(DateTime kickOff) =>
        _fixture.Build<Game>().With(g => g.Start, kickOff).With(g => g.IsFinished, false).Create();

    private void VerifyPlays(Harness h, int times) =>
        h.Launcher.Verify(p => p.PlayVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Func<Task>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Exactly(times));

    private static StreamHealthReport[] Snapshot(Harness h)
    {
        lock (h.Reports) return h.Reports.ToArray();
    }

    [Fact]
    public async Task PlayerGivesUpAsCdnUnreachable_RowHealthyOnRecheck_RestartsOnSamePoolRow_NeverReportedFailed()
    {
        var stream = CreateStream();
        var h = Build(stream, _ => Healthy(stream), new Queue<PlaybackResult>([GaveUpCdnUnreachable()]));

        var outcome = await h.Sut.StartAsync(LiveGame(DateTime.UtcNow.AddMinutes(-50)), h.Launcher.Object);

        Assert.Equal([TimeSpan.FromMinutes(2)], h.RecheckDelays);
        VerifyPlays(h, 2);
        var row = Assert.Single(h.Pool.GetHealthyStreams());
        Assert.Same(stream, row.Stream);
        Assert.Equal(0, outcome.CdnUnreachableStreamCount);
        Assert.Contains(Snapshot(h), r => r.Status == "working" && r.StreamUrl == stream.Url);
        Assert.DoesNotContain(Snapshot(h), r => r.Status is "failed" or "unknown");
    }

    [Fact]
    public async Task PlayerGivesUpAsCdnUnreachable_RecheckProvesRowDead_ReportedAndDropped_NoReplay()
    {
        var stream = CreateStream();
        var h = Build(stream, call => call == 1
                ? Healthy(stream)
                : new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Failed, ErrorMessage = "M3U8 not Found" },
            new Queue<PlaybackResult>([GaveUpCdnUnreachable()]));

        var outcome = await h.Sut.StartAsync(LiveGame(DateTime.UtcNow.AddMinutes(-50)), h.Launcher.Object);

        Assert.Equal([TimeSpan.FromMinutes(2)], h.RecheckDelays);
        VerifyPlays(h, 1);
        Assert.Empty(h.Pool.GetHealthyStreams());
        Assert.Equal(0, outcome.CdnUnreachableStreamCount);
        var failed = Assert.Single(Snapshot(h), r => r.Status == "failed");
        Assert.Equal(stream.Url, failed.StreamUrl);
    }

    [Fact]
    public async Task PlayerGivesUpAsCdnUnreachable_CdnStaysDown_RechecksTwoFiveThenTen_UntilNotLive_KeptAndNeverReported()
    {
        var kickOff = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);
        var now = kickOff.AddMinutes(2);
        var stream = CreateStream();
        var h = Build(stream, call => call == 1 ? Healthy(stream) : CdnDeferred(stream),
            new Queue<PlaybackResult>([GaveUpCdnUnreachable()]),
            clock: () => now,
            onRecheckDelay: d => now += d);

        var outcome = await h.Sut.StartAsync(LiveGame(kickOff), h.Launcher.Object);

        Assert.Equal(TimeSpan.FromMinutes(2), h.RecheckDelays[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), h.RecheckDelays[1]);
        Assert.All(h.RecheckDelays.Skip(2), d => Assert.Equal(TimeSpan.FromMinutes(10), d));
        Assert.True(now > kickOff + CdnReachabilityPolicy.MaxLiveWindow);
        VerifyPlays(h, 1);
        var row = Assert.Single(h.Pool.GetHealthyStreams());
        Assert.Same(stream, row.Stream);
        Assert.Equal(1, outcome.CdnUnreachableStreamCount);
        Assert.DoesNotContain(Snapshot(h), r => r.Status is "failed" or "unknown");
    }

    [Theory]
    [InlineData("No video (audio only)")]
    [InlineData("Playback error")]
    public async Task PlayerEndsForAnotherReason_NoPoolRecheck(string closeMessage)
    {
        var stream = CreateStream();
        var h = Build(stream, _ => Healthy(stream), new Queue<PlaybackResult>([PlaybackResult.Completed(closeMessage), PlaybackResult.Completed(closeMessage)]));

        await h.Sut.StartAsync(LiveGame(DateTime.UtcNow.AddMinutes(-50)), h.Launcher.Object);

        // The stream itself failed (after the one fresh-URL retry): the normal
        // chain reports and drops it. No CDN recheck is started for it.
        Assert.Empty(h.RecheckDelays);
        Assert.Empty(h.Pool.GetHealthyStreams());
        Assert.Contains(Snapshot(h), r => r.Status == "failed" && r.StreamUrl == stream.Url);
    }

    [Fact]
    public async Task ViewerLeft_NoPoolRecheck()
    {
        var stream = CreateStream();
        var h = Build(stream, _ => Healthy(stream),
            new Queue<PlaybackResult>([PlaybackResult.Completed(PlaybackCloseReasons.UserClosed)]));

        await h.Sut.StartAsync(LiveGame(DateTime.UtcNow.AddMinutes(-50)), h.Launcher.Object);

        Assert.Empty(h.RecheckDelays);
        VerifyPlays(h, 1);
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