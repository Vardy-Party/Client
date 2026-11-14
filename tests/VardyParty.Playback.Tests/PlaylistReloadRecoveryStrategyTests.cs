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
        Assert.Contains(logs, l => l.Contains("outcome=retry", StringComparison.Ordinal) && l.Contains("status=503", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("outcome=serve", StringComparison.Ordinal) && l.Contains("status=200", StringComparison.Ordinal));
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
    public async Task Coordinator_DeferralBudget_ServesLastGoodWhenDelayWouldExceed()
    {
        var fetches = new Queue<PlaylistReloadFetchResult>(
        [
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html"),
            PlaylistReloadFetchResult.FromStatus(503, Html, "text/html")
        ]);
        var lastGood = Playable;
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        var outcome = await RunAsync(
            firstAttach: false,
            fetches,
            delays,
            logs,
            lastGood,
            maxHold: TimeSpan.FromMilliseconds(100));

        Assert.Equal(PlaylistReloadOutcomeKind.ServeLastGood, outcome.Kind);
        Assert.Equal(lastGood, outcome.Body);
        Assert.Equal([TimeSpan.Zero], delays);
        Assert.Contains(logs, l => l.Contains("outcome=serve-last-good", StringComparison.Ordinal));
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
                Log = line => logs?.Add(line)
            },
            _ => Task.FromResult(fetches.Dequeue()),
            CancellationToken.None);
    }
}
