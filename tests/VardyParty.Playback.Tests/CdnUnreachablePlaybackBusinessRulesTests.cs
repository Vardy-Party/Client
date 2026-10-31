using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Business rules: a player error that only says the CDN could not be reached
/// (connection refused or timed out, reset, TLS) is retried like a transient
/// error, and when the retries are used up the row is kept: never reported,
/// never removed from the pool. A DNS failure (unable to resolve host) stays
/// dead. Evidence: the MSI IP block from 19:02 to 19:44 UK time on 31 Oct 2026.
/// </summary>
public class CdnUnreachablePlaybackBusinessRulesTests
{
    private const string Refused = "Source error [network connection failed] ConnectException: Failed to connect to hls.lshls.st/1.2.3.4:443: Connection refused";
    private DateTimeOffset _now = new(2026, 10, 31, 19, 5, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Refused, PlaybackErrorKind.CdnUnreachable)]
    [InlineData("Source error [network connection timeout] SocketTimeoutException: connect timed out", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("SSLHandshakeException: Connection closed by peer", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("Connection reset by peer", PlaybackErrorKind.CdnUnreachable)]
    [InlineData("UnknownHostException: Unable to resolve host \"hls.gone\": No address associated with hostname", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("Response code: 404 for index.m3u8", PlaybackErrorKind.ConfirmedDead)]
    [InlineData("decoy segment (image)", PlaybackErrorKind.ConfirmedDead)]
    public void Classification_ConnectIsCdnUnreachable_DnsAndNotFoundAreDead(string message, PlaybackErrorKind expected)
        => Assert.Equal(expected, PlaybackPolicy.ClassifyPlaybackError(message));

    [Fact]
    public void CdnUnreachable_FirstRetryReloadsThePlaylist()
    {
        Assert.True(PlaybackPolicy.ShouldRetrySameStream(PlaybackErrorKind.CdnUnreachable, 0));
        Assert.Equal(SameStreamRetryStep.ReloadPlaylist, PlaybackPolicy.NextSameStreamRetryStep(PlaybackErrorKind.CdnUnreachable, 1));
    }

    private PlaybackSessionController Established(int healthy)
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(healthy);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        return session;
    }

    /// <summary>Feeds the same error, re-attaching on each fresh /play, until the session gives up on the row.</summary>
    private static (List<PlaybackEffect> All, IReadOnlyList<PlaybackEffect> Last) ErrorUntilGivenUp(PlaybackSessionController session, string message)
    {
        var all = new List<PlaybackEffect>();
        IReadOnlyList<PlaybackEffect> last = [];
        for (var i = 0; i < 10; i++)
        {
            last = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, message));
            all.AddRange(last);
            if (last.Any(e => e.Kind is PlaybackEffectKind.AdvanceToNext or PlaybackEffectKind.CloseSession
                    or PlaybackEffectKind.RevertToLastGood))
                break;
            if (last.Any(e => e.Kind == PlaybackEffectKind.RetryFreshResolve))
                session.BeginAttach($"http://a{i}.m3u8", usedCachedUrl: false, force: true);
        }

        return (all, last);
    }

    [Fact]
    public void Established_CdnUnreachable_RetriesThenMovesOn_KeepsRowUnreported()
    {
        var session = Established(healthy: 3);

        var (all, last) = ErrorUntilGivenUp(session, Refused);

        Assert.Contains(all, e => e.Kind == PlaybackEffectKind.Attach || e.Kind == PlaybackEffectKind.RetryFreshResolve);
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.AdvanceToNext
                                   && e.Reason!.StartsWith(PlaybackCloseReasons.CdnUnreachable, StringComparison.Ordinal));
        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.ClearResolvedUrl);
        Assert.DoesNotContain(all, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void Established_CdnUnreachable_OnlyRow_ClosesWithCdnReason_NeverReports()
    {
        var session = Established(healthy: 1);

        var (all, last) = ErrorUntilGivenUp(session, Refused);

        var close = Assert.Single(last, e => e.Kind == PlaybackEffectKind.CloseSession);
        Assert.StartsWith(PlaybackCloseReasons.CdnUnreachable, close.Reason);
        Assert.True(PlaybackResult.Completed(close.Reason!).IsLocalServiceUnavailable);
        Assert.DoesNotContain(all, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void StartThatCannotReachCdn_ClosesUnreported()
    {
        var session = new PlaybackSessionController(() => _now);
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://a.m3u8");

        var (all, last) = ErrorUntilGivenUp(session, Refused);

        Assert.Contains(last, e => e.Kind == PlaybackEffectKind.CloseSession
                                   && e.Reason!.StartsWith(PlaybackCloseReasons.CdnUnreachable, StringComparison.Ordinal));
        Assert.DoesNotContain(all, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.RemoveCurrentFromPool);
    }

    [Fact]
    public void DnsFailure_StillReportsAndRemoves()
    {
        var session = Established(healthy: 3);

        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration,
            "UnknownHostException: Unable to resolve host \"hls.gone\""));

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
    }
}
