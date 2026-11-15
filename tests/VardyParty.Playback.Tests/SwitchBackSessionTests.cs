using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Playback.Tests;

public class SwitchBackSessionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-14T20:27:53Z");
    private const string LeftUrl = "https://cdn.example.com/channel-north.m3u8";
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void Timeline_BetterHeight_SwitchesBackAfterTwoGoodProbes()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);

        // Act
        var first = session.Next(T0);
        var probeDue = session.Next(T0.AddSeconds(5));
        var afterFirstGood = session.NoteProbe(playable: true, statusCode: 200, T0.AddSeconds(5), "1|a.ts");
        var secondDue = session.Next(T0.AddSeconds(20));
        var back = session.NoteProbe(playable: true, statusCode: 200, T0.AddSeconds(20), "2|b.ts");

        // Assert
        Assert.Equal(SwitchBackAction.Wait, first.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), first.Wait);
        Assert.Equal(SwitchBackAction.Probe, probeDue.Action);
        Assert.Equal(SwitchBackAction.Wait, afterFirstGood.Action);
        Assert.Equal(TimeSpan.FromSeconds(15), afterFirstGood.Wait);
        Assert.Equal(SwitchBackAction.Probe, secondDue.Action);
        Assert.Equal(SwitchBackAction.SwitchBack, back.Action);
        Assert.Contains("better height", back.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_SameHeight_WaitsMinDwellBeforeSwitchBack()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 540), T0);
        session.NoteBackupHeight(540, T0);
        session.Next(T0.AddSeconds(5));
        session.NoteProbe(true, 200, T0.AddSeconds(5), "1|a.ts");

        // Act
        var second = session.NoteProbe(true, 200, T0.AddSeconds(20), "2|b.ts");
        var back = session.Next(T0.AddSeconds(30));

        // Assert
        Assert.Equal(SwitchBackAction.Wait, second.Action);
        Assert.Contains("dwell", second.Reason, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), second.Wait);
        Assert.Equal(SwitchBackAction.SwitchBack, back.Action);
        Assert.Contains("user-chosen", back.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dwell_StartsAtTheBackupsFirstSettledPicture_NotWhenTheOldStreamWasLeft()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 540), T0);
        session.NoteBackupHeight(540, T0.AddSeconds(10));
        session.NoteProbe(true, 200, T0.AddSeconds(5), "1|a.ts");
        session.NoteProbe(true, 200, T0.AddSeconds(20), "2|b.ts");

        // Act
        var at30 = session.Next(T0.AddSeconds(30));
        var at40 = session.Next(T0.AddSeconds(40));

        // Assert
        Assert.Equal(T0.AddSeconds(10), session.BackupAttachedAt);
        Assert.Equal(SwitchBackAction.Wait, at30.Action);
        Assert.Equal(TimeSpan.FromSeconds(10), at30.Wait);
        Assert.Equal(SwitchBackAction.SwitchBack, at40.Action);
    }

    [Fact]
    public void Arm_LeavesBackupAttachedAtUnsetUntilAPictureSettles()
    {
        // Arrange
        var session = new SwitchBackSession();

        // Act
        session.Arm(Candidate(height: 720), T0);
        session.NoteBackupHeight(null, T0.AddSeconds(1));

        // Assert
        Assert.Null(session.BackupAttachedAt);
        Assert.Null(session.BackupHeight);
    }

    [Fact]
    public void FrozenPlaylist_TwoIdenticalWindows_DoNotCountAsRecovered()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5), "41|seg-41.ts");

        // Act
        var frozen = session.NoteProbe(true, 200, T0.AddSeconds(20), "41|seg-41.ts");

        // Assert
        Assert.Equal(SwitchBackAction.Wait, frozen.Action);
        Assert.Equal("playlist not moving", frozen.Reason);
        Assert.Equal(0, session.ConsecutiveGood);
    }

    [Fact]
    public void MovingPlaylist_AfterAFrozenOne_CountsAgain()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5), "41|seg-41.ts");
        session.NoteProbe(true, 200, T0.AddSeconds(20), "41|seg-41.ts");

        // Act
        session.NoteProbe(true, 200, T0.AddSeconds(50), "46|seg-46.ts");
        var back = session.NoteProbe(true, 200, T0.AddSeconds(110), "56|seg-56.ts");

        // Assert
        Assert.Equal(SwitchBackAction.SwitchBack, back.Action);
    }

    [Fact]
    public void UserNext_CancelsSwitchBack()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5), "1|a.ts");

        // Act
        session.Cancel("User next");
        var after = session.NoteProbe(true, 200, T0.AddSeconds(20), "2|b.ts");

        // Assert
        Assert.Equal(SwitchBackAction.Stop, after.Action);
        Assert.Contains("User next", after.Reason, StringComparison.Ordinal);
        Assert.Equal(SwitchBackAction.Stop, session.Next(T0.AddSeconds(20)).Action);
    }

    [Fact]
    public void BadProbe_ResetsConsecutiveGood()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5), "1|a.ts");

        // Act
        var failed = session.NoteProbe(false, 503, T0.AddSeconds(20));
        var countAfterFailure = session.ConsecutiveGood;
        session.NoteProbe(true, 200, T0.AddSeconds(50), "3|c.ts");
        var stillWaiting = session.Next(T0.AddSeconds(50));

        // Assert
        Assert.Equal(0, countAfterFailure);
        Assert.Equal(SwitchBackAction.Wait, failed.Action);
        Assert.NotEqual(SwitchBackAction.SwitchBack, stillWaiting.Action);
        Assert.Equal(1, session.ConsecutiveGood);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public void DeadOrRejectedProbe_StopsWithoutSwitchBack(int status)
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);

        // Act
        var stop = session.NoteProbe(false, status, T0.AddSeconds(5));

        // Assert
        Assert.Equal(SwitchBackAction.Stop, stop.Action);
        Assert.True(session.IsCancelled);
    }

    [Fact]
    public void ReArmBetweenProbeStartAndResult_StaleResultIsIgnored()
    {
        // Arrange
        var session = new SwitchBackSession();
        var firstArm = session.Arm(Candidate(height: 1080, url: "https://cdn.example.com/row-a.m3u8"), T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5), "1|a.ts");
        var probeFor = session.Next(T0.AddSeconds(20));
        var secondArm = session.Arm(Candidate(height: 1080, url: "https://cdn.example.com/row-b.m3u8"), T0.AddSeconds(21));

        // Act
        var stale = session.NoteProbe(firstArm, new SwitchBackProbeResult(true, 200, "2|b.ts"), T0.AddSeconds(22));

        // Assert
        Assert.Equal(firstArm, probeFor.ArmId);
        Assert.NotEqual(firstArm, secondArm);
        Assert.Equal(SwitchBackAction.Wait, stale.Action);
        Assert.Equal("stale probe ignored", stale.Reason);
        Assert.Equal(0, session.ConsecutiveGood);
        Assert.Equal("https://cdn.example.com/row-b.m3u8", session.Candidate!.PlaylistUrl);
    }

    [Fact]
    public void CancelForAStaleArm_LeavesTheNewArmArmed()
    {
        // Arrange
        var session = new SwitchBackSession();
        var firstArm = session.Arm(Candidate(height: 1080), T0);
        session.Arm(Candidate(height: 720), T0.AddSeconds(1));

        // Act
        var cancelled = session.Cancel(firstArm, "switched back");

        // Assert
        Assert.False(cancelled);
        Assert.True(session.IsArmed);
    }

    [Fact]
    public void IsSwitchBackEnabled_DefaultAndExplicit()
    {
        // Arrange
        var retryOnly = PlaylistReloadStrategyNames.RetrySameUrl;

        // Act
        var byDefault = PlaylistReloadStrategies.IsSwitchBackEnabled(null);
        var named = PlaylistReloadStrategies.IsSwitchBackEnabled(PlaylistReloadStrategyNames.RetrySameUrlThenSwitchBack);
        var unknown = PlaylistReloadStrategies.IsSwitchBackEnabled("TemporarySwitch");
        var disabled = PlaylistReloadStrategies.IsSwitchBackEnabled(retryOnly);

        // Assert
        Assert.True(byDefault);
        Assert.True(named);
        Assert.True(unknown);
        Assert.False(disabled);
    }

    [Fact]
    public void Commands_ArmOnCdnUnreachableAdvance_AndCancelOnUserNext()
    {
        // Arrange
        var session = new SwitchBackSession();
        var logs = new List<string>();
        var candidate = Candidate(1080);
        var moveOn = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: $"{PlaybackCloseReasons.CdnUnreachable}: network connection failed")
        ]);
        var userNext = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: "User next")
        ]);

        // Act
        SwitchBackCommands.Apply(session, enabled: true, moveOn, candidate, T0, logs.Add);
        var armed = session.IsArmed;
        SwitchBackCommands.Apply(session, enabled: true, userNext, candidate, T0.AddSeconds(1), logs.Add);

        // Assert
        Assert.True(armed);
        Assert.True(session.IsCancelled);
        Assert.Contains(logs, l => l.Contains("[switch-back] watching", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("[switch-back] cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loop_TwoGoodProbes_SwitchesBackAndLogs()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(1080), T0);
        session.NoteBackupHeight(648, T0);
        var clock = new FakeClock(T0);
        var probes = 0;
        var switched = 0;
        var logs = new List<string>();
        using var cts = new CancellationTokenSource();

        // Act
        await SwitchBackLoop.RunAsync(
            session,
            (_, _) => Task.FromResult(new SwitchBackProbeResult(true, 200, $"{++probes}|seg.ts")),
            (_, _, _) =>
            {
                switched++;
                cts.Cancel();
                return Task.CompletedTask;
            },
            clock.Delay,
            clock.Now,
            logs.Add,
            cts.Token);

        // Assert
        Assert.Equal(2, probes);
        Assert.Equal(1, switched);
        Assert.Contains(logs, l => l.StartsWith("[switch-back] returning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loop_ProbeThrows_KeepsProbingAndSwitchesBackWhenRecovered()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(1080), T0);
        session.NoteBackupHeight(648, T0);
        var clock = new FakeClock(T0);
        var probes = 0;
        var switched = 0;
        var logs = new List<string>();
        using var cts = new CancellationTokenSource();

        // Act
        await SwitchBackLoop.RunAsync(
            session,
            (_, _) =>
            {
                probes++;
                return probes switch
                {
                    1 => throw new HttpRequestException("Connection refused"),
                    2 => Task.FromException<SwitchBackProbeResult>(new TaskCanceledException("HttpClient.Timeout")),
                    _ => Task.FromResult(new SwitchBackProbeResult(true, 200, $"{probes}|seg.ts"))
                };
            },
            (_, _, _) =>
            {
                switched++;
                cts.Cancel();
                return Task.CompletedTask;
            },
            clock.Delay,
            clock.Now,
            logs.Add,
            cts.Token);

        // Assert
        Assert.Equal(4, probes);
        Assert.Equal(1, switched);
        Assert.Contains(logs, l => l == "[switch-back] probe failed (HttpRequestException)");
        Assert.Contains(logs, l => l == "[switch-back] probe failed (TaskCanceledException)");
    }

    [Fact]
    public async Task Loop_SwitchBackThrows_StopsAndLogs()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(1080), T0);
        session.NoteBackupHeight(648, T0);
        var clock = new FakeClock(T0);
        var logs = new List<string>();
        var probes = 0;
        var switchAttempts = 0;
        using var cts = new CancellationTokenSource();

        // Act
        var loop = SwitchBackLoop.RunAsync(
            session,
            (_, _) => Task.FromResult(new SwitchBackProbeResult(true, 200, $"{++probes}|seg.ts")),
            (_, _, _) =>
            {
                switchAttempts++;
                return Task.FromException(new InvalidOperationException("attach refused"));
            },
            clock.Delay,
            clock.Now,
            msg => { lock (logs) logs.Add(msg); },
            cts.Token);
        await WaitUntilAsync(() => session.IsCancelled);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(1, switchAttempts);
        Assert.False(session.IsArmed);
        lock (logs)
            Assert.Contains(logs, l => l.StartsWith("[switch-back] switch-back failed (InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loop_AttachReArmsDuringSwitchBack_SwitchedBackDoesNotCancelTheNewArm()
    {
        // Arrange
        var session = new SwitchBackSession();
        session.Arm(Candidate(1080), T0);
        session.NoteBackupHeight(648, T0);
        var clock = new FakeClock(T0);
        var probes = 0;
        using var cts = new CancellationTokenSource();

        // Act
        await SwitchBackLoop.RunAsync(
            session,
            (_, _) => Task.FromResult(new SwitchBackProbeResult(true, 200, $"{++probes}|seg.ts")),
            (_, _, _) =>
            {
                // The returned-to stream fails at once and the session re-arms.
                session.Arm(Candidate(720, "https://cdn.example.com/channel-south.m3u8"), clock.Now());
                cts.Cancel();
                return Task.CompletedTask;
            },
            clock.Delay,
            clock.Now,
            cancellationToken: cts.Token);

        // Assert
        Assert.True(session.IsArmed);
        Assert.Equal("https://cdn.example.com/channel-south.m3u8", session.Candidate!.PlaylistUrl);
    }

    [Fact]
    public async Task RunnerCreate_UsesSettledHeightAndSwitchesOnTheDispatcher()
    {
        // Arrange
        var controller = new PlaybackSessionController();
        var row = Row(LeftUrl, probedHeight: null);
        var switching = _fixture.GetMock<IStreamSwitchingService>();
        switching.Setup(s => s.GetCurrentStream()).Returns(row);
        switching.Setup(s => s.GetHealthyStreams()).Returns([row]);
        switching.Setup(s => s.SwitchToStream(0)).Returns(true);
        var probe = _fixture.GetMock<ISwitchBackProbe>();
        var probes = 0;
        probe.Setup(p => p.ProbeAsync(It.IsAny<SwitchBackCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SwitchBackProbeResult(true, 200, $"{++probes}|seg.ts"));
        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var clock = new FakeClock(T0);
        var runner = SwitchBackRunner.Create(
            controller,
            switching.Object,
            probe.Object,
            settledHeight: () => 1080,
            runOnUi: SwitchBackRunner.Dispatch(dispatched.Enqueue),
            delay: clock.Delay,
            clock: clock.Now);
        var moveOn = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: $"{PlaybackCloseReasons.CdnUnreachable}: network connection failed")
        ]);

        // Act
        runner.Consider(moveOn);
        var armedHeight = controller.SwitchBack.Candidate?.LastHeight;
        runner.NoteBackupHeight(648);
        await WaitUntilAsync(() => !dispatched.IsEmpty);
        var switchedBeforeDispatch = switching.Invocations.Count(i => i.Method.Name == nameof(IStreamSwitchingService.SwitchToStream));
        dispatched.TryDequeue(out var onUi);
        onUi!();
        await WaitUntilAsync(() => controller.SwitchBack.IsCancelled);
        runner.Stop();

        // Assert
        Assert.Equal(1080, armedHeight);
        Assert.Equal(0, switchedBeforeDispatch);
        switching.Verify(s => s.SwitchToStream(0), Times.Once);
        Assert.True(row.UnusedResolvedUrl);
    }

    [Fact]
    public void RunnerCreate_RowProbedHeightWinsOverTheSettledHeight()
    {
        // Arrange
        var controller = new PlaybackSessionController();
        var row = Row(LeftUrl, probedHeight: 720);
        var switching = _fixture.GetMock<IStreamSwitchingService>();
        switching.Setup(s => s.GetCurrentStream()).Returns(row);
        var runner = SwitchBackRunner.Create(
            controller,
            switching.Object,
            _fixture.GetMock<ISwitchBackProbe>().Object,
            settledHeight: () => 1080,
            runOnUi: SwitchBackRunner.Dispatch(_ => { }));
        var moveOn = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: $"{PlaybackCloseReasons.CdnUnreachable}: network connection failed")
        ]);

        // Act
        runner.Consider(moveOn);
        runner.Stop();

        // Assert
        Assert.Equal(720, controller.SwitchBack.Candidate?.LastHeight);
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:41\n#EXTINF:6,\nseg-41.ts?token=a\n#EXTINF:6,\nseg-42.ts?token=b\n", "41|seg-42.ts")]
    [InlineData("#EXTM3U\n#EXTINF:6,\nhttps://cdn.example.com/live/seg-7.ts\n", "-|https://cdn.example.com/live/seg-7.ts")]
    [InlineData("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE: 9 \n", "9|-")]
    public void PlaylistProgress_FingerprintsTheLiveWindow(string playlist, string expected)
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes(playlist);

        // Act
        var progress = SwitchBackPlaylistProgress.Of(body);

        // Assert
        Assert.Equal(expected, progress);
    }

    [Fact]
    public void PlaylistProgress_RotatingTokenOnTheSameSegment_IsNotProgress()
    {
        // Arrange
        var first = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:5\n#EXTINF:6,\nseg-5.ts?token=one\n");
        var second = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:5\n#EXTINF:6,\nseg-5.ts?token=two\n");

        // Act
        var a = SwitchBackPlaylistProgress.Of(first);
        var b = SwitchBackPlaylistProgress.Of(second);

        // Assert
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task HttpProbe_PlayablePlaylist_ReturnsProgressAndSendsRowHeaders()
    {
        // Arrange
        var handler = new RecordingHandler(HttpStatusCode.OK, "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:12\n#EXTINF:6,\nseg-12.ts\n");
        var probe = new HttpSwitchBackProbe(new HttpClient(handler));
        var candidate = Candidate(1080) with
        {
            RequestHeaders = new Dictionary<string, string> { ["Origin"] = "https://origin.example.com" }
        };

        // Act
        var result = await probe.ProbeAsync(candidate, CancellationToken.None);

        // Assert
        Assert.True(result.Playable);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("12|seg-12.ts", result.Progress);
        Assert.Equal("https://referer.example.com/", handler.Last!.Headers.Referrer?.ToString());
        Assert.Contains("https://origin.example.com", handler.Last.Headers.GetValues("Origin"));
    }

    [Fact]
    public async Task HttpProbe_GatewayPage_IsNotPlayable()
    {
        // Arrange
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, "<html>gateway</html>");
        var probe = new HttpSwitchBackProbe(new HttpClient(handler));

        // Act
        var result = await probe.ProbeAsync(Candidate(1080), CancellationToken.None);

        // Assert
        Assert.False(result.Playable);
        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.Progress);
    }

    private EnrichedStream Row(string url, int? probedHeight)
    {
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://catalog.example.com/channel-north")
            .With(s => s.Channel, "Channel North")
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.PlayerStreams, [])
            .Create();
        var built = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.ResolvedM3U8Url, url)
            .With(e => e.UnusedResolvedUrl, false)
            .With(e => e.Status, StreamResolutionStatus.Healthy);
        if (probedHeight is null)
            return built.Without(e => e.Health).Create();
        var health = _fixture.Build<StreamHealth>().With(h => h.ProbedVideoHeight, probedHeight).Create();
        return built.With(e => e.Health, health).Create();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    private static SwitchBackCandidate Candidate(int height, string url = LeftUrl) =>
        new(url, "Channel North", "https://catalog.example.com/channel-north", height, null, "https://referer.example.com/");

    private sealed class FakeClock(DateTimeOffset start)
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = start;

        public DateTimeOffset Now()
        {
            lock (_gate)
                return _now;
        }

        public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                _now += delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            await Task.Yield();
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
