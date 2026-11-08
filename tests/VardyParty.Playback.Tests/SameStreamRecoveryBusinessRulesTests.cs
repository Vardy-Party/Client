using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Business rules: a stream that is playing is retried before playback
/// switches away from it. These cover the jumps between streams seen in the
/// 31 Oct 2026 TV logs.
/// </summary>
public class SameStreamRecoveryBusinessRulesTests
{
    private DateTimeOffset _now = new(2026, 10, 31, 16, 35, 0, TimeSpan.Zero);

    private PlaybackSessionController Established(int healthy = 3, string url = "http://a.m3u8")
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(healthy);
        session.BeginAttach(url);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        return session;
    }

    private static IReadOnlyList<PlaybackEffect> Error(PlaybackSessionController s, string message)
        => s.Handle(MediaEngineEvent.Error(s.Snapshot.AttachGeneration, message));

    // ---------- classification ----------

    [Theory]
    [InlineData("Source error", PlaybackErrorKind.Transient)]
    [InlineData("Source error [network connection timeout] SocketTimeoutException: timeout", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("Response code: 404 for segment_123.ts", PlaybackErrorKind.Transient)]
    [InlineData("Response code: 404 for index.m3u8", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("410 Gone playlist", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("Source error UnknownHostException: Unable to resolve host \"cdn.x\"", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("ConnectException: Failed to connect to /1.2.3.4:443", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("No m3u8 URL returned", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("decoy segment (image)", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("[decoder init failed] MediaCodecVideoDecoderException", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("Unsupported video type / format", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("Media failed (source not supported): Exception from HRESULT: 0xC00D36C4", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("Adaptive source failed: UnsupportedManifestContentType", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("format not supported", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("ParserException: Input does not start with the #EXTM3U header", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("Response code: 403", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("HTTP 401 Unauthorized", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("HTTP 442", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("Media failed (source not supported): Unknown media error Forbidden (403). (0x80190193)", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("[parsing manifest malformed]", PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData("HTTP 504 gateway #EXTM3U", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("bitrate 4030 kbps stall", PlaybackErrorKind.Transient)]
    [InlineData(null, PlaybackErrorKind.Transient)]
    public void ClassifyPlaybackError_SeparatesTransientTokenAndDead(string? message, PlaybackErrorKind expected)
        => Assert.Equal(expected, PlaybackPolicy.ClassifyPlaybackError(message));

    [Fact]
    public void RetrySteps_ReloadThenFresh_WithBackoff()
    {
        Assert.Equal(SameStreamRetryStep.ReloadPlaylist, PlaybackPolicy.NextSameStreamRetryStep(PlaybackErrorKind.Transient, 1));
        Assert.Equal(SameStreamRetryStep.FreshResolve, PlaybackPolicy.NextSameStreamRetryStep(PlaybackErrorKind.Transient, 2));
        Assert.Equal(SameStreamRetryStep.FreshResolve, PlaybackPolicy.NextSameStreamRetryStep(PlaybackErrorKind.TokenOrPlaylistInvalid, 1));
        Assert.Equal(TimeSpan.Zero, PlaybackPolicy.SameStreamRetryBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(1), PlaybackPolicy.SameStreamRetryBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(3), PlaybackPolicy.SameStreamRetryBackoff(3));
        Assert.False(PlaybackPolicy.ShouldRetrySameStream(PlaybackErrorKind.ConfirmedDead, 0));
        Assert.False(PlaybackPolicy.ShouldRetrySameStream(PlaybackErrorKind.Transient, PlaybackPolicy.MaxSameStreamRetries));
    }

    [Fact]
    public void BuildEngineErrorMessage_CarriesCodeNameAndCause()
    {
        var message = PlaybackPolicy.BuildEngineErrorMessage("Source error", 3002, "ParserException: Input does not start with the #EXTM3U header");

        Assert.Equal("Source error [parsing manifest malformed] ParserException: Input does not start with the #EXTM3U header", message);
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, PlaybackPolicy.ClassifyPlaybackError(message));
        Assert.Null(PlaybackPolicy.DescribeExoErrorCode(9999));
        Assert.Equal("Playback error", PlaybackPolicy.BuildEngineErrorMessage(null, null, null));
    }

    // ---------- controller ----------

    [Fact]
    public void TransientError_ReloadsThenFreshThenFresh_ThenSwitches()
    {
        var session = Established();

        var first = Error(session, "Source error");
        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.Attach && e.Url == "http://a.m3u8");
        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.RaiseBuffering);
        Assert.True(session.Snapshot.IsRecovering);
        Assert.Equal(1, session.Snapshot.SameStreamRetries);

        var second = Error(session, "Source error");
        Assert.Contains(second, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        session.BeginAttach("http://a2.m3u8", usedCachedUrl: false, force: true);
        Assert.Equal(PlaybackSessionState.Buffering, session.Snapshot.State);

        var third = Error(session, "Source error");
        Assert.Contains(third, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        session.BeginAttach("http://a3.m3u8", usedCachedUrl: false, force: true);

        var last = Error(session, "Source error");
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.False(session.Snapshot.IsRecovering);
    }

    [Fact]
    public void RetryFailures_NeverReportUntilExhausted()
    {
        var session = Established();
        var all = new List<PlaybackEffect>();
        for (var i = 0; i < PlaybackPolicy.MaxSameStreamRetries; i++)
        {
            var effects = Error(session, "Source error");
            all.AddRange(effects);
            if (effects.Any(e => e.Kind == PlaybackEffectKind.RetryFreshResolve))
                session.BeginAttach($"http://a{i}.m3u8", false, true);
        }

        Assert.DoesNotContain(all, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool
            or PlaybackEffectKind.AdvanceToNext or PlaybackEffectKind.RevertToLastGood);
    }

    [Fact]
    public void MalformedPlaylist_GoesStraightToFreshPlay()
    {
        // Sole healthy row: still one fresh /play (multi-row cuts over immediately).
        var session = Established(healthy: 1);

        var effects = Error(session, "ParserException: Input does not start with the #EXTM3U header");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.Attach);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void ExpiredToken_GoesStraightToFreshPlay()
    {
        var session = Established();

        var effects = Error(session, "Response code: 403");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.Attach);
    }

    [Fact]
    public void ConfirmedDead_SwitchesImmediately()
    {
        var session = Established();

        var effects = Error(session, "UnknownHostException: Unable to resolve host \"cdn.dead\"");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Equal(0, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public void FormatWindowsMediaFailedMessage_StampsSourceNotSupportedMarker()
    {
        // Arrange
        // Act
        var stamped = PlaybackPolicy.FormatWindowsMediaFailedMessage(
            isSourceNotSupported: true,
            errorMessage: "Exception from HRESULT: 0xC00D36C4",
            extendedMessage: null);
        var network = PlaybackPolicy.FormatWindowsMediaFailedMessage(
            isSourceNotSupported: false,
            errorMessage: "network blip",
            extendedMessage: "ext");
        var forbidden = PlaybackPolicy.FormatWindowsMediaFailedMessage(
            isSourceNotSupported: true,
            errorMessage: "Unknown media error Forbidden (403). (0x80190193)");

        // Assert
        Assert.Equal("Media failed (source not supported): Exception from HRESULT: 0xC00D36C4", stamped);
        Assert.Equal(PlaybackErrorKind.ConfirmedDead, PlaybackPolicy.ClassifyPlaybackError(stamped));
        Assert.Equal("Media failed: network blip\next", network);
        Assert.Equal(PlaybackErrorKind.Transient, PlaybackPolicy.ClassifyPlaybackError(network));
        // WinRT maps HTTP 403 → SourceNotSupported; keep it as a token reject, not format death.
        Assert.Equal("Media failed: Unknown media error Forbidden (403). (0x80190193)", forbidden);
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, PlaybackPolicy.ClassifyPlaybackError(forbidden));
    }

    [Fact]
    public void UnsupportedSource_SwitchesImmediatelyWithoutSameStreamSoak()
    {
        // Arrange
        var session = Established();
        var reason = PlaybackPolicy.FormatWindowsMediaFailedMessage(
            isSourceNotSupported: true,
            errorMessage: "Exception from HRESULT: 0xC00D36C4");

        // Act
        var effects = Error(session, reason);

        // Assert — SourceNotSupported is ConfirmedDead: report, remove, advance; no reload/fresh soak.
        Assert.Equal(PlaybackErrorKind.ConfirmedDead, PlaybackPolicy.ClassifyPlaybackError(reason));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.Attach or PlaybackEffectKind.RetryFreshResolve);
        Assert.Equal(0, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public void RecoveredStream_PlaysOnWithoutSwitch()
    {
        var session = Established();
        Error(session, "Source error");

        var ready = session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        Assert.False(session.Snapshot.IsRecovering);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal("http://a.m3u8", session.Snapshot.CurrentUrl);
        Assert.DoesNotContain(ready, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void Budget_IsSharedWithinWindow_AndRefillsAfter90s()
    {
        var session = Established();
        Error(session, "Source error");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Within 90s the stream is flapping: the next error uses attempt 2 (fresh /play).
        _now = _now.AddSeconds(30);
        var flapping = Error(session, "Source error");
        Assert.Contains(flapping, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.Equal(2, session.Snapshot.SameStreamRetries);
        session.BeginAttach("http://a2.m3u8", false, true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Long after, a new blip starts a fresh budget again (a reload).
        _now = _now.AddSeconds(91);
        var later = Error(session, "Source error");
        Assert.Contains(later, e => e.Kind == PlaybackEffectKind.Attach && e.Url == "http://a2.m3u8");
        Assert.Equal(1, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public void HealthyCountProvider_PreventsClosingWhenPoolGrewAfterStart()
    {
        // The session was told "1 healthy" at start; the resolver added 2 more since.
        var session = Established(healthy: 1);
        session.HealthyStreamCountProvider = () => 3;

        var effects = Error(session, "Unable to resolve host cdn.dead");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
    }

    [Fact]
    public void HealthDecline_GetsOneReloadOnly()
    {
        var session = Established(healthy: 2);
        List<PlaybackEffect> Stall()
        {
            var all = new List<PlaybackEffect>();
            for (var i = 0; i < 4; i++)
                all.AddRange(session.Handle(MediaEngineEvent.Buffering(session.Snapshot.AttachGeneration, true)));
            return all;
        }

        var first = Stall();
        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.Attach);

        // Buffering while the reload runs is not a second decline.
        Assert.DoesNotContain(Stall(), e => e.Kind is PlaybackEffectKind.ReportDeclined or PlaybackEffectKind.AdvanceToNext);

        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        var second = Stall();
        Assert.Contains(second, e => e.Kind == PlaybackEffectKind.ReportDeclined);
        Assert.Contains(second, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void DownloadFailureBurst_UsesOneAttemptNotTheWholeBudget()
    {
        var session = Established();
        for (var i = 0; i < PlaybackPolicy.MaxConsecutiveDownloadFailures + 3; i++)
            session.NotifyDownloadFailure("segment 404");

        Assert.Equal(1, session.Snapshot.SameStreamRetries);
        Assert.True(session.Snapshot.IsRecovering);
    }

    [Fact]
    public void EmptyFreshPlayDuringRecovery_KeepsScraping_ThenMovesOnWithoutRemoving()
    {
        // Arrange
        var session = Established();
        Error(session, "Response code: 403");
        Assert.Equal(1, session.Snapshot.SameStreamRetries);
        var cap = PlaybackPolicy.MaxSameStreamRetries + PlaybackPolicy.EmptyFreshPlayExtraAttempts;

        // Act
        IReadOnlyList<PlaybackEffect> last = [];
        var freshPlays = 0;
        for (var i = 0; i < cap; i++)
        {
            last = session.NotifyFreshResolveUnavailable();
            if (last.Any(e => e.Kind == PlaybackEffectKind.RetryFreshResolve))
                freshPlays++;
            else
                break;
        }

        // Assert
        Assert.Equal(cap - 1, freshPlays);
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        Assert.DoesNotContain(last, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void MalformedPlaylist_WithOtherStreams_AdvancesImmediately_NoFreshWait()
    {
        // Arrange — Bravia 8 Nov 2026: Hadcrack non-#EXTM3U closed to home after a
        // late fresh /play; keep the player up and cut over when the pool has peers.
        var session = Established(healthy: 3);
        var effects = Error(
            session,
            "Source error [parsing manifest malformed] ParserException: Input does not start with the #EXTM3U header");

        // Assert — seamless: no RetryFreshResolve black-screen wait, no Close
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void MalformedPlaylist_SoleStream_StillTriesOneFreshPlay()
    {
        var session = Established(healthy: 1);
        var first = Error(
            session,
            "Source error [parsing manifest malformed] ParserException: Input does not start with the #EXTM3U header");

        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(first, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }

    [Fact]
    public void MalformedPlaylist_ThenEmptyFreshPlay_AdvancesImmediately()
    {
        // Arrange — TV black screen 4 Nov 2026: #EXTM3U then four ~20s /play NotFounds
        // Sole healthy row: still one fresh /play, then advance (no EmptyFreshPlay loop).
        var session = Established(healthy: 1);
        session.ConfigureCycleTracking(null, null, () => true);
        var first = Error(session, "Source error [parsing manifest malformed] ParserException: Input does not start with the #EXTM3U header");

        // Act
        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        session.SetHealthyStreamCount(3);
        var afterEmpty = session.NotifyFreshResolveUnavailable("Fresh M3U8 unavailable");

        // Assert — one scrape max; do not keep EmptyFreshPlay looping
        Assert.Contains(afterEmpty, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(afterEmpty, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(afterEmpty, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void UserNavigation_CancelsRecovery_AndNewStreamGetsFullBudget()
    {
        var session = Established();
        Error(session, "Source error");

        session.Handle(MediaEngineEvent.UserNext());
        Assert.False(session.Snapshot.IsRecovering);
        session.BeginAttach("http://b.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        Assert.Equal(0, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public void CancelSameStreamRecovery_ResetsBudget()
    {
        var session = Established();
        Error(session, "Source error");

        session.CancelSameStreamRecovery();

        Assert.False(session.Snapshot.IsRecovering);
        Assert.Equal(0, session.Snapshot.SameStreamRetries);
    }

    [Fact]
    public void FailedSwitch_StillRevertsToLastGood_MakeBeforeBreak()
    {
        var session = Established(url: "http://good.m3u8");
        session.BeginAttach("http://candidate.m3u8");

        var effects = SameStreamRetryDriver.FailUntilSwitch(session, "Source error");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.False(session.Snapshot.IsRecovering);
    }

    [Fact]
    public void StartError_WithFreshUrl_RetriesOnce_ButDeadErrorDropsAtOnce()
    {
        var retried = new PlaybackSessionController();
        retried.SetHealthyStreamCount(2);
        retried.BeginAttach("http://s.m3u8", usedCachedUrl: false);
        var transient = Error(retried, "Source error");
        Assert.Contains(transient, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.DoesNotContain(transient, e => e.Kind == PlaybackEffectKind.ReportFailed);

        var dead = new PlaybackSessionController();
        dead.SetHealthyStreamCount(2);
        dead.BeginAttach("http://s.m3u8", usedCachedUrl: false);
        var effects = Error(dead, "Response code: 404 for index.m3u8");
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
    }
}