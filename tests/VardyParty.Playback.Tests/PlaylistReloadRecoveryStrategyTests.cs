using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaylistReloadRecoveryStrategyTests
{
    private static readonly byte[] Playable = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:1,\nseg.ts\n");
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html>gateway 503 https://cdn.example/token=secret</html>");

    private static PlaylistReloadObservation Obs(
        int attempt,
        bool first,
        int status,
        byte[]? body = null,
        string? type = null) =>
        new(attempt, first, status, body ?? [], type);

    [Fact]
    public void Decide_PlayablePlaylist_Serves()
    {
        var decision = PlaylistReloadStrategies.Default.Decide(Obs(1, first: true, 200, Playable));
        Assert.Equal(PlaylistReloadAction.Serve, decision.Action);
    }

    [Fact]
    public void Decide_FirstAttach503_GivesUpWithoutRetry()
    {
        var decision = PlaylistReloadStrategies.Default.Decide(Obs(1, first: true, 503, Html, "text/html"));
        Assert.Equal(PlaylistReloadAction.GiveUp, decision.Action);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, decision.ErrorKind);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void Decide_MidPlay503_RetriesWithSameStreamBackoff(int attempt, int expectedDelaySeconds)
    {
        var decision = PlaylistReloadStrategies.Default.Decide(Obs(attempt, first: false, 503, Html, "text/html"));
        Assert.Equal(PlaylistReloadAction.Retry, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), decision.Delay);
    }

    [Fact]
    public void Decide_MidPlay503Attempt3_GivesUpCdnUnreachable()
    {
        var decision = PlaylistReloadStrategies.Default.Decide(Obs(3, first: false, 503, Html, "text/html"));
        Assert.Equal(PlaylistReloadAction.GiveUp, decision.Action);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, decision.ErrorKind);
    }

    [Fact]
    public void Decide_MidPlayHtmlBody_RetriesThenGivesUpAsMalformed()
    {
        var first = PlaylistReloadStrategies.Default.Decide(Obs(1, first: false, 200, Html, "text/html"));
        Assert.Equal(PlaylistReloadAction.Retry, first.Action);

        var last = PlaylistReloadStrategies.Default.Decide(Obs(3, first: false, 200, Html, "text/html"));
        Assert.Equal(PlaylistReloadAction.GiveUp, last.Action);
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, last.ErrorKind);
    }

    [Theory]
    [InlineData(401, PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData(403, PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData(442, PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData(404, PlaybackErrorKind.ConfirmedDead)]
    [InlineData(410, PlaybackErrorKind.ConfirmedDead)]
    public void Decide_MidPlayAuthOrGone_GivesUpImmediately(int status, PlaybackErrorKind kind)
    {
        var decision = PlaylistReloadStrategies.Default.Decide(Obs(1, first: false, status));
        Assert.Equal(PlaylistReloadAction.GiveUp, decision.Action);
        Assert.Equal(kind, decision.ErrorKind);
    }

    [Fact]
    public void Resolve_UnknownName_FallsBackToRetrySameUrl()
    {
        Assert.Equal(PlaylistReloadStrategyNames.RetrySameUrl, PlaylistReloadStrategies.Resolve(null).Name);
        Assert.Equal(PlaylistReloadStrategyNames.RetrySameUrl, PlaylistReloadStrategies.Resolve("TemporarySwitch").Name);
    }

    [Fact]
    public async Task Coordinator_503Then200_ServesRecoveredPlaylist()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(200, Playable, "application/vnd.apple.mpegurl")
        ]);
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            delays,
            logs);

        Assert.Equal(PlaylistReloadOutcomeKind.Serve, outcome.Kind);
        Assert.Equal(Playable, outcome.Body);
        Assert.Equal([TimeSpan.Zero], delays);
        Assert.Contains(logs, l => l.StartsWith("INFO ", StringComparison.Ordinal) && l.Contains("outcome=retry", StringComparison.Ordinal) && l.Contains("status=503", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("INFO ", StringComparison.Ordinal) && l.Contains("outcome=serve", StringComparison.Ordinal) && l.Contains("attempt=2", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("token=secret", StringComparison.Ordinal) || l.Contains("cdn.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_Three503s_GivesUpClassifiedCdnUnreachable()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html")
        ]);
        var delays = new List<TimeSpan>();

        var outcome = await RunAsync(firstAttach: false, fetches, delays);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Contains("HTTP 503", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, PlaybackPolicy.ClassifyPlaybackError(outcome.Message));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1)], delays);
        Assert.Empty(fetches);
    }

    [Fact]
    public async Task Coordinator_ThreeHtmlBodies_GivesUpClassifiedMalformed()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(200, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(200, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(200, Html, "text/html")
        ]);

        var outcome = await RunAsync(firstAttach: false, fetches, new List<TimeSpan>());

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, outcome.ErrorKind);
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, PlaybackPolicy.ClassifyPlaybackError(outcome.Message));
        Assert.Contains("#EXTM3U", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_FirstAttach503_DoesNotRetry()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(200, Playable)
        ]);
        var delays = new List<TimeSpan>();

        var outcome = await RunAsync(firstAttach: true, fetches, delays);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Empty(delays);
        Assert.Single(fetches); // the recovered 200 was never requested
    }

    [Fact]
    public async Task Coordinator_DeferralBudget_GivesUpAsCdnUnreachableEvenWhenLastGoodExists()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html")
        ]);
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            delays,
            logs,
            lastGood: Playable,
            maxHold: TimeSpan.FromMilliseconds(100));

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Contains("HTTP 503", outcome.Message, StringComparison.Ordinal);
        Assert.Equal([TimeSpan.Zero], delays);
        Assert.Contains(logs, l => l.Contains("outcome=give-up (deferral budget)", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("serve-last-good", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_DeferralBudget_GivesUpWhenNoLastGood()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html")
        ]);

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            new List<TimeSpan>(),
            maxHold: TimeSpan.FromMilliseconds(100));

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Contains("HTTP 503", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MidPlayRequest_IsSharedAcrossPlatforms_NoLastGoodAndSameHoldBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackPolicy.PlaylistReloadHoldBudget);
        Assert.Equal(PlaybackPolicy.PlaylistReloadHoldBudget, PlaybackPolicy.AndroidPlaylistHoldBudget);
        Assert.True(PlaybackPolicy.WindowsPlaylistDeferralBudget > PlaybackPolicy.PlaylistReloadHoldBudget);

        var session = new PlaylistReloadSession(PlaylistReloadStrategies.Default, "Match Wawes");
        session.FirstPlaylistCompleted = true;
        var android = PlaylistReloadRequest.MidPlay(isFirstAttach: false, session, "Match Wawes");
        var windows = PlaylistReloadRequest.MidPlay(isFirstAttach: false, session, "Match Wawes");
        var linux = PlaylistReloadRequest.MidPlay(isFirstAttach: false, session, "Match Wawes");

        foreach (var request in new[] { android, windows, linux })
        {
            Assert.Null(request.LastGoodPlaylist);
            Assert.Equal(PlaybackPolicy.PlaylistReloadHoldBudget, request.MaxHold);
            Assert.False(request.IsFirstAttach);
            Assert.Same(session, request.Session);
            Assert.Equal("Match Wawes", request.Row);
        }
    }

    [Fact]
    public async Task MidPlay_Slow503_GivesUpAsCdnUnreachableWithoutServingLastGood()
    {
        var now = DateTimeOffset.Parse("2026-11-14T20:20:14Z");
        var session = new PlaylistReloadSession(PlaylistReloadStrategies.Default, "Match Wawes");
        session.FirstPlaylistCompleted = true;
        var logs = new List<string>();
        var request = PlaylistReloadRequest.MidPlay(
            isFirstAttach: false,
            session,
            "Match Wawes",
            log: (isInfo, line) => logs.Add(isInfo ? $"INFO {line}" : $"DEBUG {line}"),
            delay: (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            clock: () => now);

        var outcome = await PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            request,
            _ =>
            {
                now += TimeSpan.FromSeconds(3.3);
                return Task.FromResult(PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"));
            },
            CancellationToken.None);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.DoesNotContain(logs, l => l.Contains("serve-last-good", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("give-up (deferral budget)", StringComparison.Ordinal));
    }

    [Fact]
    public void Logging_RedactsUrlsAndTrimsBody()
    {
        var line = PlaylistReloadLogging.Format(
            "Match Wawes",
            attempt: 1,
            status: 503,
            contentType: "text/html",
            body: Html,
            outcome: "retry",
            delayMs: 0);
        Assert.StartsWith("[playlist-retry] row=\"Match Wawes\" attempt=1 status=503", line, StringComparison.Ordinal);
        Assert.Contains("outcome=retry delay_ms=0", line, StringComparison.Ordinal);
        Assert.Contains("https://(redacted)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", line, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "serve", false)]
    [InlineData(1, "retry", true)]
    [InlineData(1, "give-up", true)]
    [InlineData(1, "give-up (deferral budget)", true)]
    [InlineData(2, "serve", true)]
    [InlineData(3, "serve", true)]
    public void Logging_IsInfo_OnlyWhenAttemptOverOneOrOutcomeIsNotServe(int attempt, string outcome, bool expected) =>
        Assert.Equal(expected, PlaylistReloadLogging.IsInfo(attempt, outcome));

    [Fact]
    public async Task Coordinator_FirstTryServe_LogsAtDebug()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(200, Playable, "application/vnd.apple.mpegurl")
        ]);
        var logs = new List<string>();

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            new List<TimeSpan>(),
            logs);

        Assert.Equal(PlaylistReloadOutcomeKind.Serve, outcome.Kind);
        var line = Assert.Single(logs);
        Assert.StartsWith("DEBUG ", line, StringComparison.Ordinal);
        Assert.Contains("row=\"Match Wawes\"", line, StringComparison.Ordinal);
        Assert.Contains("attempt=1", line, StringComparison.Ordinal);
        Assert.Contains("status=200", line, StringComparison.Ordinal);
        Assert.Contains("outcome=serve", line, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome=serve-last-good", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_StatusZeroThen200_RecoversOnAttemptThreeAtInfo()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(0),
            PlaylistReloadFetchResult.FromStatus(0),
            PlaylistReloadFetchResult.FromStatus(200, Playable, "application/vnd.apple.mpegurl")
        ]);
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            delays,
            logs);

        Assert.Equal(PlaylistReloadOutcomeKind.Serve, outcome.Kind);
        Assert.Equal(Playable, outcome.Body);
        Assert.Equal(2, delays.Count);
        Assert.Contains(logs, l => l.StartsWith("INFO ", StringComparison.Ordinal) && l.Contains("attempt=1", StringComparison.Ordinal) && l.Contains("status=0", StringComparison.Ordinal) && l.Contains("outcome=retry", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("INFO ", StringComparison.Ordinal) && l.Contains("attempt=2", StringComparison.Ordinal) && l.Contains("status=0", StringComparison.Ordinal) && l.Contains("outcome=retry", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("INFO ", StringComparison.Ordinal) && l.Contains("attempt=3", StringComparison.Ordinal) && l.Contains("status=200", StringComparison.Ordinal) && l.Contains("outcome=serve", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.StartsWith("DEBUG ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_Slow503WithLastGood_GivesUpWithinHoldBudget()
    {
        var now = DateTimeOffset.Parse("2026-11-14T20:20:14Z");
        var session = new PlaylistReloadSession(PlaylistReloadStrategies.Default, "Match Wawes");
        session.FirstPlaylistCompleted = true;
        var logs = new List<string>();

        var outcome = await PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            new PlaylistReloadRequest
            {
                IsFirstAttach = false,
                LastGoodPlaylist = Playable,
                MaxHold = PlaybackPolicy.AndroidPlaylistHoldBudget,
                Row = "Match Wawes",
                Session = session,
                Delay = (delay, _) =>
                {
                    now += delay;
                    return Task.CompletedTask;
                },
                Clock = () => now,
                Log = (isInfo, line) => logs.Add(isInfo ? $"INFO {line}" : $"DEBUG {line}")
            },
            _ =>
            {
                now += TimeSpan.FromSeconds(3.3);
                return Task.FromResult(PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"));
            },
            CancellationToken.None);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Null(session.LastHeldLastGoodAt);
        Assert.Contains(logs, l => l.Contains("give-up (deferral budget)", StringComparison.Ordinal) && l.Contains("status=503", StringComparison.Ordinal));
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, PlaybackPolicy.ClassifyPlaybackError(outcome.Message));
    }

    [Fact]
    public async Task Coordinator_TimeBudget_GivesUpCdnUnreachableAfterHeldReloads()
    {
        var started = DateTimeOffset.Parse("2026-11-14T20:20:14Z");
        var now = started;
        var session = new PlaylistReloadSession(PlaylistReloadStrategies.Default, "Match Wawes");
        session.FirstPlaylistCompleted = true;
        session.LastGoodPlaylist = Playable;
        session.NoteFailure(started);
        now = started + PlaybackPolicy.PlaylistReloadGiveUpBudget;

        var outcome = await PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            new PlaylistReloadRequest
            {
                IsFirstAttach = false,
                LastGoodPlaylist = Playable,
                MaxHold = PlaybackPolicy.AndroidPlaylistHoldBudget,
                Row = "Match Wawes",
                Session = session,
                Delay = (_, _) => Task.CompletedTask,
                Clock = () => now
            },
            _ => Task.FromResult(PlaylistReloadFetchResult.FromStatus(503, Html, "text/html")),
            CancellationToken.None);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Contains("time budget", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, PlaybackPolicy.ClassifyPlaybackError(outcome.Message));
    }

    [Fact]
    public void FromHttp_Empty200_IsStatusZero()
    {
        var empty = PlaylistReloadFetchResult.FromHttp(200, [], "application/vnd.apple.mpegurl");
        Assert.Equal(0, empty.StatusCode);
        Assert.Empty(empty.Body);

        var playable = PlaylistReloadFetchResult.FromHttp(200, Playable, "application/vnd.apple.mpegurl");
        Assert.Equal(200, playable.StatusCode);
        Assert.Equal(Playable, playable.Body);
    }

    [Fact]
    public async Task Coordinator_StatusZeroGiveUp_StampsCdnUnreachableAndSurvivesLaterParserException()
    {
        var session = new PlaylistReloadSession(PlaylistReloadStrategies.Default, "Match Wawes");
        session.FirstPlaylistCompleted = true;
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(0),
            PlaylistReloadFetchResult.FromStatus(0),
            PlaylistReloadFetchResult.FromStatus(0)
        ]);

        var outcome = await PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            new PlaylistReloadRequest
            {
                IsFirstAttach = false,
                Row = "Match Wawes",
                Session = session,
                Delay = (_, _) => Task.CompletedTask,
                Clock = () => DateTimeOffset.Parse("2026-11-14T20:27:20Z")
            },
            _ => Task.FromResult(fetches.Dequeue()),
            CancellationToken.None);

        Assert.Equal(PlaylistReloadOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, outcome.ErrorKind);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, session.LastGiveUpKind);
        Assert.Equal(outcome.Message, session.LastGiveUpMessage);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, PlaybackPolicy.ClassifyPlaybackError(outcome.Message));

        // TV 257 at 20:27: after give-up, ExoPlayer kept the live buffer then
        // raised ParserException (~DesiredLiveOffsetSeconds later). That must
        // not take the Hadcrack tried+failed path.
        var laterParse = PlaybackPolicy.BuildEngineErrorMessage(
            "Source error",
            3002,
            "ParserException: Input does not start with the #EXTM3U header");
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, PlaybackPolicy.ClassifyPlaybackError(laterParse));
        var resolved = PlaybackPolicy.ResolvePlayerErrorAfterPlaylistGiveUp(laterParse, session);
        Assert.Equal(outcome.Message, resolved);
        Assert.Equal(PlaybackErrorKind.CdnUnreachable, PlaybackPolicy.ClassifyPlaybackError(resolved));
    }

    [Fact]
    public void ResolvePlayerErrorAfterPlaylistGiveUp_WithoutGiveUp_KeepsParserException()
    {
        var session = new PlaylistReloadSession();
        var parse = PlaybackPolicy.BuildEngineErrorMessage(
            "Source error",
            3002,
            "ParserException: Input does not start with the #EXTM3U header");
        Assert.Equal(parse, PlaybackPolicy.ResolvePlayerErrorAfterPlaylistGiveUp(parse, session));
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, PlaybackPolicy.ClassifyPlaybackError(parse));
    }

    private static Task<PlaylistReloadOutcome> RunAsync(
        bool firstAttach,
        Queue<PlaylistReloadFetchResult> fetches,
        List<TimeSpan> delays,
        List<string>? logs = null,
        byte[]? lastGood = null,
        TimeSpan? maxHold = null)
    {
        var now = DateTimeOffset.Parse("2026-11-14T18:00:00Z");
        return PlaylistReloadCoordinator.RunAsync(
            PlaylistReloadStrategies.Default,
            new PlaylistReloadRequest
            {
                IsFirstAttach = firstAttach,
                LastGoodPlaylist = lastGood,
                MaxHold = maxHold,
                Row = "Match Wawes",
                Delay = (delay, _) =>
                {
                    delays.Add(delay);
                    now += delay;
                    return Task.CompletedTask;
                },
                Clock = () => now,
                Log = (isInfo, line) => logs?.Add(isInfo ? $"INFO {line}" : $"DEBUG {line}")
            },
            _ => Task.FromResult(fetches.Dequeue()),
            CancellationToken.None);
    }
}
