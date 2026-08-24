using System.Collections.Generic;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Core.Tests;

/// <summary>
/// Locks the unified recovery rules that used to differ between Windows and Android.
/// Each test names the platform gap, then asserts the Core decision both OS hosts must follow.
/// </summary>
public class PlaybackUnificationRulesTests
{
    [Fact]
    public void FailedSwitch_WindowsReverted_AndroidAdvanced_UnifiedReverts()
    {
        var session = PlayingThenSwitching();
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void EstablishedHardFail_WindowsDidNotRemove_UnifiedRemovesAndAdvances()
    {
        var session = Established(healthy: 3);
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "MediaFailed"));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void FailedStart_WindowsMediaFailedClosedImmediately_UnifiedAdvancesIfPoolRemains()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://start.m3u8");

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "MediaFailed"));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
    }

    [Fact]
    public void SoftDecline_WindowsHadNone_UnifiedUsesAndroidWindowOnAllOs()
    {
        var session = Established(healthy: 2);
        var gen = session.Snapshot.AttachGeneration;

        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, true));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void Buffering_AndroidHookWasNoOp_UnifiedAlwaysRaises()
    {
        var session = Established(healthy: 1);
        var effects = session.Handle(MediaEngineEvent.Buffering(session.Snapshot.AttachGeneration, true));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
    }

    [Fact]
    public void ConsecutiveDownloadFailures_WindowsOnly_UnifiedHardFailAtFive()
    {
        Assert.Equal(5, PlaybackPolicy.MaxConsecutiveDownloadFailures);

        var session = Established(healthy: 3);
        for (var i = 0; i < 4; i++)
            session.NotifyDownloadFailure();

        var effects = session.NotifyDownloadFailure();
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void ClearedExoPlayerError_MustNotBecomeEngineError()
        => Assert.True(PlaybackPolicy.ShouldIgnoreClearedEngineError(true));

    [Fact]
    public void UserNavigate_NeverRemovesFromPool_OnEitherOs()
    {
        var session = Established(healthy: 2);
        foreach (var nav in new[] { MediaEngineEvent.UserNext(), MediaEngineEvent.UserPrevious() })
        {
            var effects = session.Handle(nav);
            Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
            Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        }
    }

    private static PlaybackSessionController Established(int healthy)
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(healthy);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        return session;
    }

    private static PlaybackSessionController PlayingThenSwitching()
    {
        var session = Established(3);
        session.BeginAttach("http://bad.m3u8");
        return session;
    }
}
