using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackSessionControllerTests
{
    [Fact]
    public void BeginAttach_ThenReady_EstablishesAndReportsWorking()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);

        var attach = session.BeginAttach("http://a.m3u8", usedCachedUrl: true);
        Assert.Contains(attach, e => e.Kind == PlaybackEffectKind.Attach && e.Url == "http://a.m3u8");
        Assert.Equal(PlaybackSessionState.Starting, session.Snapshot.State);

        var gen = session.Snapshot.AttachGeneration;
        // Act
        var ready = session.Handle(MediaEngineEvent.Ready(gen));

        // Assert
        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.MarkEstablished);
        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.True(session.Snapshot.HasEstablishedPlayback);
        Assert.Equal("http://a.m3u8", session.Snapshot.LastGoodUrl);
    }

    [Fact]
    public void StaleError_IsIgnored()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.BeginAttach("http://b.m3u8");
        // Act
        var effects = session.Handle(MediaEngineEvent.Error(gen, "old failure"));

        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
    }

    [Fact]
    public void FailedStart_WithCachedUrl_RequestsFreshResolve()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        var gen = session.Snapshot.AttachGeneration;

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(gen, "403"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.True(session.Snapshot.CacheRetryUsed);
    }

    [Fact]
    public void FailedStart_AfterCacheRetry_AdvancesWhenPoolRemains()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        var gen1 = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Error(gen1, "403"));

        // Host resolved fresh and attaches again without cache.
        var attach = session.BeginAttach("http://fresh.m3u8", usedCachedUrl: false, force: true);
        Assert.Contains(attach, e => e.Kind == PlaybackEffectKind.Attach);
        var gen2 = session.Snapshot.AttachGeneration;

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(gen2, "still broken"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(1, session.Snapshot.HealthyStreamCount);
    }

    [Fact]
    public void FailedStart_SoleStream_ClosesSession()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        var gen = session.Snapshot.AttachGeneration;

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(gen, "boom"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public void FailedSwitch_RevertsToLastGood_AndRemovesBroken()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        session.BeginAttach("http://bad.m3u8");
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == "http://good.m3u8");
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal("http://good.m3u8", session.Snapshot.CurrentUrl);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void EstablishedHardFail_AdvancesWithoutRevert()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void SoftDecline_AdvancesAndReportsDeclined()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, isBuffering: true));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void Buffering_AlwaysRaisesEffect()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        // Act
        var effects = session.Handle(MediaEngineEvent.Buffering(gen, true));
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.Equal(PlaybackSessionState.Buffering, session.Snapshot.State);

        effects = session.Handle(MediaEngineEvent.Buffering(gen, false));
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void UserNext_DoesNotMarkBad_OnlyAdvances()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserNext());

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Equal(2, session.Snapshot.HealthyStreamCount);
    }

    [Fact]
    public void UserNext_BlockedWhenOnlyOneStream()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserNext());
        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void UserReportBad_RemovesAndAdvances()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserReportBad("glitchy"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void UserClose_StopsAndCloses()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserClose());
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.Stop);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession && e.Reason == "User closed");
        Assert.Equal(PlaybackSessionState.Closed, session.Snapshot.State);
    }

    [Fact]
    public void PlaybackEnded_DoesNotAutoAdvance()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        // Act
        var effects = session.Handle(MediaEngineEvent.Ended(gen));

        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.None);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
    }

    [Fact]
    public void NotifyFreshResolveUnavailable_FailsStart()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403"));

        // Act
        var effects = session.NotifyFreshResolveUnavailable();
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void StaleReady_IsIgnored()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.BeginAttach("http://b.m3u8");
        // Act
        var effects = session.Handle(MediaEngineEvent.Ready(gen));

        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
        Assert.Equal("http://a.m3u8", session.Snapshot.LastGoodUrl);
    }

    [Fact]
    public void BeginAttach_SameUrlWhilePlaying_IsRejected()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.BeginAttach("http://a.m3u8");
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.None);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void BeginAttach_Force_RebindsSameUrl()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.BeginAttach("http://a.m3u8", force: true);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.Attach);
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
    }

    [Fact]
    public void ClosedSession_IgnoresError()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.Handle(MediaEngineEvent.UserClose());

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "late"));
        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void FailedStart_WithoutCache_AdvancesWhenPoolRemains()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://fresh.m3u8", usedCachedUrl: false);
        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403"));

        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void UserPrevious_DoesNotMarkBad()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserPrevious());
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToPrevious);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void UserReportBad_LastStream_ClosesSession()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.UserReportBad("bad"));
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public void BufferingDuringStart_DoesNotDecline()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, true));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Equal(PlaybackSessionState.Starting, session.Snapshot.State);
    }

    [Fact]
    public void MetricsBitrateDecline_AdvancesAndReportsDeclined()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));
        session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));
        // Act
        var effects = session.Handle(MediaEngineEvent.Metrics(gen, bitrateKbps: 100));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void DownloadFailures_BelowThreshold_DoNotRecover()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures - 1; i++)
            effects = session.NotifyDownloadFailure("404");

        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(4, session.Snapshot.ConsecutiveDownloadFailures);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void DownloadFailures_AtThresholdWhilePlaying_AdvanceWithoutRevert()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures; i++)
            effects = session.NotifyDownloadFailure("404");

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void DownloadFailures_AtThresholdWhileSwitching_Reverts()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures; i++)
            effects = session.NotifyDownloadFailure("404");

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void FailedLastGood_AfterRevert_AdvancesInsteadOfRevertLoop()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");
        session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal("http://good.m3u8", session.Snapshot.CurrentUrl);

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "last-good also died"));
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void BeginAttach_WhilePreparing_IsRejected()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        // Act
        var effects = session.BeginAttach("http://b.m3u8");
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.None);
        Assert.Equal("http://a.m3u8", session.Snapshot.CurrentUrl);
        Assert.True(session.Snapshot.IsPreparing);
    }

    [Fact]
    public void NotifyDownloadFailure_ResetsOnNewAttach()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.NotifyDownloadFailure();
        session.NotifyDownloadFailure();
        Assert.Equal(2, session.Snapshot.ConsecutiveDownloadFailures);

        // Act
        session.BeginAttach("http://b.m3u8", force: true);

        // Assert
        Assert.Equal(0, session.Snapshot.ConsecutiveDownloadFailures);
    }

    [Fact]
    public void NotifyDownloadSuccess_ResetsConsecutiveFailures()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("https://cdn.example.com/a.m3u8");
        session.NotifyDownloadFailure();
        session.NotifyDownloadFailure();

        // Act
        session.NotifyDownloadSuccess();

        // Assert
        Assert.Equal(0, session.Snapshot.ConsecutiveDownloadFailures);
    }

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        // Act
        session.Reset();

        // Assert
        Assert.Equal(PlaybackSessionState.Idle, session.Snapshot.State);
        Assert.False(session.Snapshot.HasEstablishedPlayback);
        Assert.Equal(0, session.Snapshot.AttachGeneration);
        Assert.Null(session.Snapshot.CurrentUrl);
    }

    [Fact]
    public void EstablishedHardFail_LastStream_ClosesSession()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        // Assert
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public void BeginAttach_AfterClosed_IsRejected()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.Handle(MediaEngineEvent.UserClose());

        // Act
        var effects = session.BeginAttach("http://b.m3u8");
        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.None);
        Assert.Equal(PlaybackSessionState.Closed, session.Snapshot.State);
    }

    [Fact]
    public void UserNext_WhilePreparing_AbandonsPrepareAndAdvances()
    {
        // Arrange — ExoPlayer can stick in BUFFERING without Ready; user must escape.
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        Assert.True(session.Snapshot.IsPreparing);

        // Act
        var effects = session.Handle(MediaEngineEvent.UserNext());

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.False(session.Snapshot.IsPreparing);
    }

    [Fact]
    public void UserNext_WhilePreparing_ThenBeginAttach_AcceptsNextUrl()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://stuck.m3u8");

        // Act
        session.Handle(MediaEngineEvent.UserNext());
        var attachEffects = session.BeginAttach("http://next.m3u8", usedCachedUrl: true);

        // Assert
        Assert.Contains(attachEffects, e => e.Kind == PlaybackEffectKind.Attach && e.Url == "http://next.m3u8");
        Assert.True(session.Snapshot.IsPreparing);
        Assert.Equal("http://next.m3u8", session.Snapshot.CurrentUrl);
    }

    [Fact]
    public void StaleBuffering_IsIgnored()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));
        session.BeginAttach("http://b.m3u8");

        // Act
        var effects = session.Handle(MediaEngineEvent.Buffering(gen, true));
        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Equal(PlaybackSessionState.Switching, session.Snapshot.State);
        Assert.False(session.Snapshot.IsBuffering);
    }
}
