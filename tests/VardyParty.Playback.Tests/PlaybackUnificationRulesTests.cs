using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Tests;

/// <summary>
/// Locks the unified recovery rules that used to differ between Windows and Android.
/// Each test names the platform gap, then asserts the Core decision both OS hosts must follow.
/// </summary>
public class PlaybackUnificationRulesTests
{
    [Fact]
    public void FailedSwitch_WindowsReverted_AndroidAdvanced_UnifiedReverts()
    {
        // Arrange
        var session = PlayingThenSwitching();

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void EstablishedHardFail_WindowsDidNotRemove_UnifiedRemovesAndAdvances()
    {
        // Arrange
        var session = Established(healthy: 3);

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "MediaFailed"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void FailedStart_WindowsMediaFailedClosedImmediately_UnifiedAdvancesIfPoolRemains()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://start.m3u8");

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "MediaFailed"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
    }

    [Fact]
    public void SoftDecline_WindowsHadNone_UnifiedUsesAndroidWindowOnAllOs()
    {
        // Arrange
        var session = Established(healthy: 2);
        var gen = session.Snapshot.AttachGeneration;

        // Act
        IReadOnlyList<PlaybackEffect> effects = [];
        for (var i = 0; i < 4; i++)
            effects = session.Handle(MediaEngineEvent.Buffering(gen, true));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void Buffering_AndroidHookWasNoOp_UnifiedAlwaysRaises()
    {
        // Arrange
        var session = Established(healthy: 1);

        // Act
        var effects = session.Handle(MediaEngineEvent.Buffering(session.Snapshot.AttachGeneration, true));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
    }

    [Fact]
    public void ConsecutiveDownloadFailures_WindowsOnly_UnifiedHardFailAtFive()
    {
        // Arrange
        var session = Established(healthy: 3);
        for (var i = 0; i < 4; i++)
            session.NotifyDownloadFailure();

        // Act
        var effects = session.NotifyDownloadFailure();

        // Assert
        Assert.Equal(5, PlaybackPolicy.MaxConsecutiveDownloadFailures);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void ClearedExoPlayerError_MustNotBecomeEngineError()
    {
        // Arrange
        const bool errorIsNull = true;

        // Act
        var ignored = PlaybackPolicy.ShouldIgnoreClearedEngineError(errorIsNull);

        // Assert
        Assert.True(ignored);
    }

    [Fact]
    public void UserNavigate_NeverRemovesFromPool_OnEitherOs()
    {
        // Arrange
        var session = Established(healthy: 2);
        var navigations = new[] { MediaEngineEvent.UserNext(), MediaEngineEvent.UserPrevious() };

        // Act
        var effects = navigations.Select(nav => session.Handle(nav)).ToList();

        // Assert
        Assert.All(effects, batch =>
        {
            Assert.DoesNotContain(batch, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
            Assert.DoesNotContain(batch, e => e.Kind == PlaybackEffectKind.ReportFailed);
        });
    }

    [Fact]
    public void EstablishedHardFail_LastStream_UnifiedCloses()
    {
        // Arrange
        var session = Established(healthy: 1);

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "MediaFailed"));

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
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
