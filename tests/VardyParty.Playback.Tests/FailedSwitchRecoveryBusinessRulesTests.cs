using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Failed switch business rules (docs/STREAM_PLAYBACK_RULES.md, "Failed switch: retry
/// the target, keep it"). A switch target (a row switched to while another stream was
/// playing) gets the same classification and same-stream budget as a playing stream:
/// a token/playlist blip or transient error retries the target (reload, then a fresh
/// <c>/play</c>); retries used up go back to last good and KEEP the target, unreported;
/// only a confirmed-dead target is reported and removed; the local service being
/// unavailable never reports. Regression: 4Kstream (edge.wedd48c.top/premium155,
/// 1080p, ranked #2) was reported and dropped on its first "Input does not start with
/// the #EXTM3U header" at 20:26:20 UK on 31 Oct 2026 (techydeals at 20:26:04).
/// Every host runs these through <see cref="PlaybackSessionController"/>,
/// <see cref="PlaybackCommandExecutor"/> and <see cref="PlaybackPoolCommandActions"/>.
/// </summary>
public class FailedSwitchRecoveryBusinessRulesTests
{
    private const string GoalUrl = "https://edge.wedd48c.top/premium109/index.m3u8?t=1793478144.a";
    private const string FourKUrl = "https://edge.wedd48c.top/premium155/index.m3u8?t=1793478380.b";
    private const string ExtM3uBlip =
        "Source error [parsing manifest malformed] ParserException: Input does not start with the #EXTM3U header";

    private readonly DateTimeOffset _now = new(2026, 10, 31, 20, 26, 20, TimeSpan.Zero);

    private PlaybackSessionController PlayingThenSwitching(string target = FourKUrl)
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(3);
        session.BeginAttach(GoalUrl);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach(target, usedCachedUrl: false, force: true);
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
        return session;
    }

    private static IReadOnlyList<PlaybackEffect> Error(PlaybackSessionController s, string message)
        => s.Handle(MediaEngineEvent.Error(s.Snapshot.AttachGeneration, message));

    private static void AssertNoReportNoRemove(IEnumerable<PlaybackEffect> effects)
        => Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed
            or PlaybackEffectKind.ReportDeclined or PlaybackEffectKind.RemoveCurrentFromPool
            or PlaybackEffectKind.CloseSession);

    // ---------- controller ----------

    [Fact]
    public void ExtM3uBlipOnSwitch_RetriesTargetWithFreshPlay_ThenPlays()
    {
        var session = PlayingThenSwitching();

        var first = Error(session, ExtM3uBlip);

        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(first, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        AssertNoReportNoRemove(first);
        Assert.True(session.Snapshot.IsRecoveringSwitch);
        Assert.Equal(1, session.Snapshot.SameStreamRetries);

        session.BeginAttach(FourKUrl + "&fresh=1", usedCachedUrl: false, force: true);
        var ready = session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal(FourKUrl + "&fresh=1", session.Snapshot.LastGoodUrl);
        Assert.False(session.Snapshot.IsRecoveringSwitch);
    }

    [Fact]
    public void TransientErrorOnSwitch_ReloadsTargetFirst()
    {
        var session = PlayingThenSwitching();

        var first = Error(session, "Source error [network connection timeout] SocketTimeoutException: timeout");

        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.Attach && e.Url == FourKUrl);
        AssertNoReportNoRemove(first);
        Assert.DoesNotContain(first, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void SwitchRetriesExhausted_RevertsToLastGood_KeepsTargetUnreported()
    {
        var session = PlayingThenSwitching();

        var last = SameStreamRetryDriver.FailUntilSwitch(session, ExtM3uBlip);

        var revert = Assert.Single(last, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.Equal(GoalUrl, revert.Url);
        AssertNoReportNoRemove(last);
        Assert.DoesNotContain(last, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Burned one-shot token: the next visit gets a fresh /play.
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal(GoalUrl, session.Snapshot.CurrentUrl);
        Assert.False(session.Snapshot.IsRecovering);
        Assert.False(session.Snapshot.IsRecoveringSwitch);
    }

    [Theory]
    [InlineData("Source error UnknownHostException: Unable to resolve host \"edge.wedd48c.top\"")]
    [InlineData("Response code: 404 for index.m3u8")]
    [InlineData("No m3u8 URL returned")]
    public void ConfirmedDeadSwitch_ReportsRemovesAndReverts(string message)
    {
        var session = PlayingThenSwitching();

        var effects = Error(session, message);

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == GoalUrl);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
    }

    [Fact]
    public void DeadDuringSwitchRetry_ReportsRemovesAndReverts()
    {
        var session = PlayingThenSwitching();
        Error(session, ExtM3uBlip);
        session.BeginAttach(FourKUrl + "&fresh=1", usedCachedUrl: false, force: true);

        var effects = Error(session, "Response code: 410 Gone playlist");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == GoalUrl);
    }

    [Fact]
    public void LocalServiceUnavailableDuringSwitchRetry_RevertsWithoutReportOrClose()
    {
        var session = PlayingThenSwitching();
        Error(session, ExtM3uBlip);

        var effects = session.NotifyFreshResolveInfrastructureUnavailable("GET /play exceeded 20s");

        AssertNoReportNoRemove(effects);
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.Stop or PlaybackEffectKind.ClearResolvedUrl);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == GoalUrl);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void EmptyFreshPlayForSwitchTarget_SpendsBudget_ThenRevertsUnreported()
    {
        var session = PlayingThenSwitching();
        var all = new List<PlaybackEffect>(Error(session, ExtM3uBlip));

        IReadOnlyList<PlaybackEffect> effects;
        var rounds = 0;
        do
        {
            effects = session.NotifyFreshResolveUnavailable("No fresh M3U8");
            all.AddRange(effects);
            Assert.True(++rounds < 10);
        }
        while (!effects.Any(e => e.Kind == PlaybackEffectKind.RevertToLastGood));

        AssertNoReportNoRemove(all);
        Assert.Equal(GoalUrl, session.Snapshot.CurrentUrl);
    }

    [Fact]
    public void FirstStartFailure_IsUnchanged_NotASwitch()
    {
        // No last good stream: the start path (one fresh retry, then report) is untouched.
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(3);
        session.BeginAttach(FourKUrl);

        var effects = Error(session, ExtM3uBlip);

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.False(session.Snapshot.IsRecoveringSwitch);
    }

    // ---------- pool + executor (what every host runs) ----------

    private static EnrichedStream Row(string channel, string? m3u8) => new()
    {
        Stream = new StreamModel { Channel = channel, Url = $"https://{channel.ToLowerInvariant()}.example/ac-milan-vs-inter" },
        ResolvedM3U8Url = m3u8,
        Status = StreamResolutionStatus.Healthy,
    };

    private sealed class Host(PlaybackPoolCommandActions actions) : IPlaybackCommandHost
    {
        public List<string> Reported { get; } = [];
        public List<(string Url, bool Revert)> Attached { get; } = [];
        public bool Removed { get; private set; }
        public bool Closed { get; private set; }

        public void BeginIndexSwitchSuppression() { }
        public void EndIndexSwitchSuppression() { }
        public void ClearCurrentResolvedUrl() => actions.ClearCurrentResolvedUrl();
        public void RemoveCurrentFromPool() { Removed = true; actions.RemoveCurrentFromPool(); }
        public void SyncHealthyStreamCount() => actions.SyncHealthyStreamCount();
        public void ReportFailed(string? reason) => Reported.Add(reason ?? "");
        public void ReportDeclined(string? reason) => Reported.Add(reason ?? "");
        public void ReportWorking() { }
        public void MarkEstablished() { }
        public void RaiseBuffering(bool isBuffering) { }
        public void Attach(string url, bool isRevert) => Attached.Add((url, isRevert));
        public void RevertPoolIndexTo(string url) => actions.RevertIndexToPlayingUrl(url);
        public void AttachCurrentAfterRemove() { }
        public void RetryFreshResolve() { }
        public void StopEngine() { }
        public void CloseSession(string reason) => Closed = true;
        public void SwitchPoolToNext() { }
        public void SwitchPoolToPrevious() { }
        public void NotifyApplyFailed(Exception exception) => throw exception;
    }

    private (PlaybackSessionController Session, StreamSwitchingService Pool, Host Host) SwitchedTo4K()
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("Serie A", "AC Milan", "Inter");
        pool.AddHealthyStream(Row("Goal stream", GoalUrl));
        pool.AddHealthyStream(Row("Besteverstr", "https://dur123.durrfity.shop/simpch12/index.m3u8"));
        pool.AddHealthyStream(Row("4Kstream", FourKUrl));
        var session = new PlaybackSessionController(() => _now);
        var actions = new PlaybackPoolCommandActions(session, pool, null, (_, _, _) => { }, _ => { });
        var host = new Host(actions);

        session.BeginAttach(GoalUrl);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        pool.SwitchToStream(2);
        session.BeginAttach(FourKUrl, usedCachedUrl: false, force: true);
        return (session, pool, host);
    }

    [Fact]
    public void Executor_ExhaustedSwitch_KeepsTargetInPool_AndPointsIndexBackAtLastGood()
    {
        var (session, pool, host) = SwitchedTo4K();

        var last = SameStreamRetryDriver.FailUntilSwitch(session, ExtM3uBlip);
        PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(last), host);

        Assert.Empty(host.Reported);
        Assert.False(host.Removed);
        Assert.False(host.Closed);
        Assert.Equal(3, pool.GetHealthyStreams().Count);
        Assert.Equal("4Kstream", pool.GetHealthyStreams()[2].Stream.Channel);
        Assert.Null(pool.GetHealthyStreams()[2].ResolvedM3U8Url);
        Assert.Equal((GoalUrl, true), host.Attached.Last());
        Assert.Equal("Goal stream", pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(1, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Executor_DeadSwitch_ReportsRemoves_AndPointsIndexBackAtLastGood()
    {
        var (session, pool, host) = SwitchedTo4K();

        var effects = Error(session, "Source error UnknownHostException: Unable to resolve host \"edge.wedd48c.top\"");
        PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(effects), host);

        Assert.Single(host.Reported);
        Assert.True(host.Removed);
        Assert.Equal(["Goal stream", "Besteverstr"], pool.GetHealthyStreams().Select(s => s.Stream.Channel));
        Assert.Equal("Goal stream", pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal((GoalUrl, true), host.Attached.Last());
    }

    [Fact]
    public void RevertIndexToPlayingUrl_NoMatchingRow_LeavesIndex()
    {
        var (_, pool, _) = SwitchedTo4K();
        var actions = new PlaybackPoolCommandActions(new PlaybackSessionController(), pool, null, (_, _, _) => { }, _ => { });

        Assert.False(actions.RevertIndexToPlayingUrl("https://nowhere.example/x.m3u8"));
        Assert.False(actions.RevertIndexToPlayingUrl(null));
        Assert.Equal(3, pool.GetCurrentStreamIndex());
        Assert.True(actions.RevertIndexToPlayingUrl(GoalUrl));
        Assert.Equal(1, pool.GetCurrentStreamIndex());
    }
}
