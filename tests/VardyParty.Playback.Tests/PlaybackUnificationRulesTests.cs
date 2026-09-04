using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

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
    public void PlaybackEnded_IsNoOp_HostsCompleteSuccessThemselves()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://oak-lane.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        // Act
        var effects = session.Handle(MediaEngineEvent.Ended(gen));
        var cmd = PlaybackCommand.FromEffects(effects);

        // Assert
        Assert.True(cmd.IsNoOp);
        Assert.False(cmd.CloseSession);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void FreshM3U8Accept_UsesSessionUrl_NotAHostLocalField()
    {
        // Arrange — Android used to compare fresh == Activity._m3u8Url (attach target).
        var session = new PlaybackSessionController();
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var sessionUrl = session.Snapshot.CurrentUrl;
        const string hostLocalUrl = "http://oak-lane-playing.m3u8";
        const string fresh = "http://oak-lane-playing.m3u8";

        // Act
        var acceptVsSession = PlaybackPolicy.ShouldAcceptFreshM3U8(sessionUrl, fresh);
        var acceptVsHostField = PlaybackPolicy.ShouldAcceptFreshM3U8(hostLocalUrl, fresh);

        // Assert
        Assert.True(acceptVsSession);
        Assert.False(acceptVsHostField);
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

    [Fact]
    public void LiveHlsSoftRecover_AndroidBehindLiveWindow_AndWindowsNetwork_ShareBudget()
    {
        // Arrange — Android ExoPlayer BLWE + Windows MediaFailed(Network) before raising Error.
        const string playingUrl = "http://oak-lane-live.m3u8";

        // Act
        var androidSignal = PlaybackPolicy.IsBehindLiveWindowFailure(
            PlaybackPolicy.ExoPlayerErrorCodeBehindLiveWindow,
            message: "Source error",
            causeSummary: null);
        var androidMessageFallback = PlaybackPolicy.IsBehindLiveWindowFailure(
            errorCode: null,
            message: "Caused by: BehindLiveWindowException",
            causeSummary: null);
        var windowsNetwork = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: true,
            isDecodingError: false,
            isUnknownError: false,
            isSourceNotSupported: false,
            isAborted: false);
        var canRecover = PlaybackPolicy.ShouldAttemptLiveHlsRecovery(0, playingUrl);

        // Assert
        Assert.Equal(5, PlaybackPolicy.MaxLiveHlsRecoveries);
        Assert.Equal(25, PlaybackPolicy.DesiredLiveOffsetSeconds);
        Assert.Equal(30_000, PlaybackPolicy.AndroidMinBufferMs);
        Assert.Equal(60_000, PlaybackPolicy.AndroidMaxBufferMs);
        Assert.Equal(2_500, PlaybackPolicy.AndroidBufferForPlaybackMs);
        Assert.Equal(8_000, PlaybackPolicy.AndroidBufferForPlaybackAfterRebufferMs);
        Assert.Equal(1002, PlaybackPolicy.ExoPlayerErrorCodeBehindLiveWindow);
        Assert.True(androidSignal);
        Assert.True(androidMessageFallback);
        Assert.True(windowsNetwork);
        Assert.True(canRecover);
    }

    [Fact]
    public void LiveHlsSoftRecover_ExhaustedBudget_BothOsEscalateToError()
    {
        // Arrange
        const string playingUrl = "http://oak-lane-live.m3u8";

        // Act
        var stillUnderBudget = PlaybackPolicy.ShouldAttemptLiveHlsRecovery(
            PlaybackPolicy.MaxLiveHlsRecoveries - 1,
            playingUrl);
        var exhausted = PlaybackPolicy.ShouldAttemptLiveHlsRecovery(
            PlaybackPolicy.MaxLiveHlsRecoveries,
            playingUrl);
        var noUrl = PlaybackPolicy.ShouldAttemptLiveHlsRecovery(0, null);

        // Assert — hosts then raise MediaEngineEvent.Error → pool remove (covered elsewhere).
        Assert.True(stillUnderBudget);
        Assert.False(exhausted);
        Assert.False(noUrl);
    }

    [Fact]
    public void LiveHlsSoftRecover_PermanentFailures_DoNotRecover_OnEitherOs()
    {
        // Arrange
        // Decoding/Unknown must escalate (network-only soft-recover, mirrors Android BLWE-only).

        // Act
        var windowsUnsupported = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: false,
            isDecodingError: false,
            isUnknownError: false,
            isSourceNotSupported: true,
            isAborted: false);
        var windowsAborted = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: true,
            isDecodingError: false,
            isUnknownError: false,
            isSourceNotSupported: false,
            isAborted: true);
        var windowsAuth = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: true,
            isDecodingError: false,
            isUnknownError: false,
            isSourceNotSupported: false,
            isAborted: false,
            detailMessage: "HTTP 403 Forbidden");
        var windowsDecoding = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: false,
            isDecodingError: true,
            isUnknownError: false,
            isSourceNotSupported: false,
            isAborted: false);
        var windowsUnknown = PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
            isNetworkError: false,
            isDecodingError: false,
            isUnknownError: true,
            isSourceNotSupported: false,
            isAborted: false);
        var notBehindLive = PlaybackPolicy.IsBehindLiveWindowFailure(
            errorCode: 2004,
            message: "Decoder init failed",
            causeSummary: null);

        // Assert
        Assert.False(windowsUnsupported);
        Assert.False(windowsAborted);
        Assert.False(windowsAuth);
        Assert.False(windowsDecoding);
        Assert.False(windowsUnknown);
        Assert.False(notBehindLive);
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
