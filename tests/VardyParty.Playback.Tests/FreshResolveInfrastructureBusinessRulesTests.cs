using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Business rules (31 Oct 2026), every host: an in-player fresh <c>/play</c>
/// (failed-start retry or same-stream retry) that hits the local service being
/// down is retried with <see cref="InfrastructureRetryPolicy.FreshResolveDelays"/>.
/// If the service never answers, the session closes "Local service unavailable":
/// no ReportFailed, no RemoveCurrentFromPool, no advance. Only "the service
/// answered with no playlist" still fails the stream.
/// </summary>
public class FreshResolveInfrastructureBusinessRulesTests
{
    private sealed class Blip(bool retryable = true) : InfrastructureUnavailableException("GET /play exceeded 20s")
    {
        public override bool IsRetryable => retryable;
    }

    private readonly DateTimeOffset _now = new(2026, 10, 31, 16, 35, 0, TimeSpan.Zero);

    private static EnrichedStream Row(string channel, string url) => new()
    {
        Stream = new Stream { Channel = channel, Url = $"http://northgate.test/{channel}" },
        ResolvedM3U8Url = url,
        Status = StreamResolutionStatus.Healthy,
        Referer = $"http://northgate.test/{channel}"
    };

    private sealed record Sut(
        PlaybackSessionController Session,
        StreamSwitchingService Pool,
        PlaybackPoolCommandActions Actions,
        List<PlaybackCommand> Applies,
        List<string> Attaches,
        List<TimeSpan> Delays,
        Func<int> Resolves);

    private Sut Create(Func<int, string?> resolve, PlaybackSessionController? session = null)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("northgate-league", "Oak Lane", "Northgate");
        pool.AddHealthyStream(Row("oak-lane", "http://oak-lane-cached.m3u8"));
        pool.AddHealthyStream(Row("river-side", "http://river-side.m3u8"));
        session ??= new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);

        var applies = new List<PlaybackCommand>();
        var attaches = new List<string>();
        var delays = new List<TimeSpan>();
        var calls = 0;
        var actions = new PlaybackPoolCommandActions(
            session,
            pool,
            (_, _) =>
            {
                var n = calls++;
                try
                {
                    return Task.FromResult(resolve(n));
                }
                catch (Exception ex)
                {
                    return Task.FromException<string?>(ex);
                }
            },
            attachViaSession: (url, _, _) => attaches.Add(url),
            applyCommand: applies.Add)
        {
            DelayAsync = d =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            }
        };
        return new Sut(session, pool, actions, applies, attaches, delays, () => calls);
    }

    private static void AssertDeferredNotDropped(Sut sut)
    {
        var cmd = Assert.Single(sut.Applies);
        Assert.False(cmd.ReportFailed);
        Assert.False(cmd.ReportDeclined);
        Assert.False(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.True(cmd.CloseSession);
        Assert.StartsWith(PlaybackCloseReasons.LocalServiceUnavailable, cmd.CloseReason);
        Assert.True(PlaybackResult.Completed(cmd.CloseReason!, true).IsLocalServiceUnavailable);
        Assert.Equal(2, sut.Pool.GetHealthyStreams().Count);
    }

    [Fact]
    public async Task FailedStartRetry_BlipThenUrl_RetriesAndAttaches_NothingReported()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var sut = Create(n => n == 0 ? throw new Blip() : "http://oak-lane-fresh.m3u8", session);

        await sut.Actions.RetryFreshResolveAsync();

        Assert.Equal(2, sut.Resolves());
        Assert.Equal([InfrastructureRetryPolicy.FreshResolveDelays[0]], sut.Delays);
        Assert.Equal(["http://oak-lane-fresh.m3u8"], sut.Attaches);
        Assert.Empty(sut.Applies);
    }

    [Fact]
    public async Task FailedStartRetry_ServiceNeverAnswers_ClosesLocalServiceUnavailable_NoReportNoRemove()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var sut = Create(_ => throw new Blip(), session);

        await sut.Actions.RetryFreshResolveAsync();

        Assert.Equal(1 + InfrastructureRetryPolicy.FreshResolveDelays.Length, sut.Resolves());
        Assert.Equal(InfrastructureRetryPolicy.FreshResolveDelays, sut.Delays);
        Assert.Empty(sut.Attaches);
        AssertDeferredNotDropped(sut);
    }

    [Fact]
    public async Task NonRetryable_MpDisabledOrAuth_DefersAtOnce()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var sut = Create(_ => throw new Blip(retryable: false), session);

        await sut.Actions.RetryFreshResolveAsync();

        Assert.Equal(1, sut.Resolves());
        Assert.Empty(sut.Delays);
        AssertDeferredNotDropped(sut);
    }

    [Fact]
    public async Task SameStreamRecovery_ServiceDown_DoesNotReportOrJumpStreams()
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://oak-lane-cached.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        var retry = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "Response code: 403"));
        Assert.Contains(retry, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        var sut = Create(_ => throw new Blip(), session);

        await sut.Actions.RetryFreshResolveAsync();

        AssertDeferredNotDropped(sut);
        Assert.False(session.Snapshot.IsRecovering);
        Assert.Equal(0, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public async Task UserMovesOnDuringInfrastructureWait_RetryIsDropped()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        Sut? sut = null;
        sut = Create(_ =>
        {
            // The viewer picks another row while the first wait runs.
            sut!.Pool.SwitchToNextStream();
            throw new Blip();
        }, session);

        await sut.Actions.RetryFreshResolveAsync();

        Assert.Equal(1, sut.Resolves());
        Assert.Empty(sut.Applies);
        Assert.Empty(sut.Attaches);
    }

    [Fact]
    public async Task ServiceAnswersNoPlaylist_IsStillAFailedStart()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var sut = Create(_ => null, session);

        await sut.Actions.RetryFreshResolveAsync();

        var cmd = Assert.Single(sut.Applies);
        Assert.True(cmd.ReportFailed);
        Assert.True(cmd.RemoveCurrentFromPool);
    }

    [Fact]
    public void Controller_InfrastructureUnavailable_AfterClose_IsNoOp()
    {
        var session = new PlaybackSessionController(() => _now);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.UserClose());

        var effects = session.NotifyFreshResolveInfrastructureUnavailable("blip");

        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void Controller_InfrastructureUnavailable_NeverReportsOrRemoves()
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");

        var effects = session.NotifyFreshResolveInfrastructureUnavailable("GET /play exceeded 20s");

        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.ReportDeclined
            or PlaybackEffectKind.RemoveCurrentFromPool or PlaybackEffectKind.ClearResolvedUrl or PlaybackEffectKind.AdvanceToNext);
        var close = Assert.Single(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.Equal($"{PlaybackCloseReasons.LocalServiceUnavailable}: GET /play exceeded 20s", close.Reason);
        Assert.Equal(3, session.Snapshot.HealthyStreamCount);
    }
}