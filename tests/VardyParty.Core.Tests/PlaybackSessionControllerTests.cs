using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Core.Tests;

public class PlaybackSessionControllerTests
{
    [Fact]
    public void BeginAttach_ThenReady_EstablishesAndReportsWorking()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);

        var attach = session.BeginAttach("http://a.m3u8", usedCachedUrl: true);
        Assert.Contains(attach, e => e.Kind == PlaybackEffectKind.Attach && e.Url == "http://a.m3u8");
        Assert.Equal(PlaybackSessionState.Starting, session.Snapshot.State);

        var gen = session.Snapshot.AttachGeneration;
        var ready = session.Handle(MediaEngineEvent.Ready(gen));

        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.MarkEstablished);
        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.True(session.Snapshot.HasEstablishedPlayback);
        Assert.Equal("http://a.m3u8", session.Snapshot.LastGoodUrl);
    }

    [Fact]
    public void StaleError_IsIgnored()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.BeginAttach("http://b.m3u8");
        var effects = session.Handle(MediaEngineEvent.Error(gen, "old failure"));

        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
    }

    [Fact]
    public void FailedStart_WithCachedUrl_RequestsFreshResolve()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        var gen = session.Snapshot.AttachGeneration;

        var effects = session.Handle(MediaEngineEvent.Error(gen, "403"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.True(session.Snapshot.CacheRetryUsed);
    }

    [Fact]
    public void FailedStart_AfterCacheRetry_AdvancesWhenPoolRemains()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        var gen1 = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Error(gen1, "403"));

        // Host resolved fresh and attaches again without cache.
        var attach = session.BeginAttach("http://fresh.m3u8", usedCachedUrl: false, force: true);
        Assert.Contains(attach, e => e.Kind == PlaybackEffectKind.Attach);
        var gen2 = session.Snapshot.AttachGeneration;

        var effects = session.Handle(MediaEngineEvent.Error(gen2, "still broken"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(1, session.Snapshot.HealthyStreamCount);
    }

    [Fact]
    public void FailedStart_SoleStream_ClosesSession()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        var gen = session.Snapshot.AttachGeneration;

        var effects = session.Handle(MediaEngineEvent.Error(gen, "boom"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public void FailedSwitch_RevertsToLastGood_AndRemovesBroken()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        session.BeginAttach("http://bad.m3u8");
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == "http://good.m3u8");
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal("http://good.m3u8", session.Snapshot.CurrentUrl);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void EstablishedHardFail_AdvancesWithoutRevert()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void SoftDecline_AdvancesAndReportsDeclined()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, isBuffering: true));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void Buffering_AlwaysRaisesEffect()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        var effects = session.Handle(MediaEngineEvent.Buffering(gen, true));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.Equal(PlaybackSessionState.Buffering, session.Snapshot.State);

        effects = session.Handle(MediaEngineEvent.Buffering(gen, false));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void UserNext_DoesNotMarkBad_OnlyAdvances()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserNext());

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Equal(2, session.Snapshot.HealthyStreamCount);
    }

    [Fact]
    public void UserNext_BlockedWhenOnlyOneStream()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserNext());
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void UserReportBad_RemovesAndAdvances()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserReportBad("glitchy"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void UserClose_StopsAndCloses()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserClose());
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.Stop);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession && e.Reason == "User closed");
        Assert.Equal(PlaybackSessionState.Closed, session.Snapshot.State);
    }

    [Fact]
    public void PlaybackEnded_DoesNotAutoAdvance()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        var effects = session.Handle(MediaEngineEvent.Ended(gen));
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void NotifyFreshResolveUnavailable_FailsStart()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403"));

        var effects = session.NotifyFreshResolveUnavailable();
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void StaleReady_IsIgnored()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.BeginAttach("http://b.m3u8");
        var effects = session.Handle(MediaEngineEvent.Ready(gen));

        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
        Assert.Equal("http://a.m3u8", session.Snapshot.LastGoodUrl);
    }

    [Fact]
    public void BeginAttach_SameUrlWhilePlaying_IsRejected()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.BeginAttach("http://a.m3u8");
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.None);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void BeginAttach_Force_RebindsSameUrl()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.BeginAttach("http://a.m3u8", force: true);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.Attach);
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
    }

    [Fact]
    public void ClosedSession_IgnoresError()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.Handle(MediaEngineEvent.UserClose());

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "late"));
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void FailedStart_WithoutCache_AdvancesWhenPoolRemains()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://fresh.m3u8", usedCachedUrl: false);
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403"));

        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void UserPrevious_DoesNotMarkBad()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserPrevious());
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToPrevious);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void UserReportBad_LastStream_ClosesSession()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var effects = session.Handle(MediaEngineEvent.UserReportBad("bad"));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public void BufferingDuringStart_DoesNotDecline()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, true));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Equal(PlaybackSessionState.Starting, session.Snapshot.State);
    }

    [Fact]
    public void MetricsBitrateDecline_AdvancesAndReportsDeclined()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));
        session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));
        var effects = session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void DownloadFailures_BelowThreshold_DoNotRecover()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures - 1; i++)
            effects = session.NotifyDownloadFailure("404");

        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(4, session.Snapshot.ConsecutiveDownloadFailures);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void DownloadFailures_AtThresholdWhilePlaying_AdvanceWithoutRevert()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures; i++)
            effects = session.NotifyDownloadFailure("404");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void DownloadFailures_AtThresholdWhileSwitching_Reverts()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures; i++)
            effects = session.NotifyDownloadFailure("404");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void FailedLastGood_AfterRevert_AdvancesInsteadOfRevertLoop()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");
        session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal("http://good.m3u8", session.Snapshot.CurrentUrl);

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "last-good also died"));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }
}
