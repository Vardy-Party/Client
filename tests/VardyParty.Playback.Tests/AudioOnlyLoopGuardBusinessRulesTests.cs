using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using Stream = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Business rules from the 31 Oct 2026 Android TV loop: an audio-only feed (READY, 0x0,
/// looping AAC) was reported working, became "last good", and Next/auto-advance kept
/// going back to it. docs/STREAM_PLAYBACK_RULES.md "Audio-only streams" and "No loops".
/// </summary>
public class AudioOnlyLoopGuardBusinessRulesTests
{
    private DateTimeOffset _now = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    private PlaybackSessionController NewSession(int healthy = 3, Func<bool>? untried = null)
    {
        var session = new PlaybackSessionController(() => _now);
        session.EnableVideoConfirmation();
        session.SetHealthyStreamCount(healthy);
        if (untried != null)
            session.ConfigureCycleTracking(null, null, untried);
        return session;
    }

    private static IReadOnlyList<PlaybackEffect> Ready(PlaybackSessionController s)
        => s.Handle(MediaEngineEvent.Ready(s.Snapshot.AttachGeneration));

    private static IReadOnlyList<PlaybackEffect> Probe(PlaybackSessionController s, bool hasVideo)
        => s.Handle(MediaEngineEvent.VideoProbe(s.Snapshot.AttachGeneration, hasVideo));

    private PlaybackSessionController PlayingWithVideo(string url, int healthy = 3, Func<bool>? untried = null)
    {
        var s = NewSession(healthy, untried);
        s.BeginAttach(url);
        Ready(s);
        Probe(s, hasVideo: true);
        return s;
    }

    // ---------- Rule 1: no-video detection ----------

    [Fact]
    public void ReadyWithoutVideo_IsNotWorking_AndNotLastGood()
    {
        var s = NewSession();
        s.BeginAttach("http://cdn/audio.m3u8");

        var ready = Ready(s);

        Assert.DoesNotContain(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Null(s.Snapshot.LastGoodUrl);
        Assert.False(s.Snapshot.IsVideoConfirmed);
    }

    [Fact]
    public void NoVideoForTimeout_FailsWithNoVideoReason_ReportsFailed_NeverWorking()
    {
        var s = NewSession();
        s.BeginAttach("http://cdn/audio.m3u8");
        Ready(s);

        _now += TimeSpan.FromSeconds(5);
        Assert.All(Probe(s, false), e => Assert.Equal(PlaybackEffectKind.None, e.Kind));

        _now += TimeSpan.FromSeconds(4);
        var failed = Probe(s, false);

        Assert.Contains(failed, e => e.Kind == PlaybackEffectKind.ReportFailed && e.Reason == PlaybackPolicy.NoVideoReason);
        Assert.DoesNotContain(failed, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Contains(failed, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Null(s.Snapshot.LastGoodUrl);
        Assert.True(s.IsFeedFailedInCycle("http://cdn/audio.m3u8?token=new"));
    }

    [Fact]
    public void NoVideoTimeout_IsConfigurable()
    {
        var s = new PlaybackSessionController(() => _now);
        s.EnableVideoConfirmation(TimeSpan.FromSeconds(3));
        s.SetHealthyStreamCount(2);
        s.BeginAttach("http://cdn/audio.m3u8");
        Ready(s);

        _now += TimeSpan.FromSeconds(3.5);

        Assert.Contains(Probe(s, false), e => e.Kind == PlaybackEffectKind.ReportFailed);
    }

    [Fact]
    public void VideoConfirm_ReportsWorkingOnce_AndSetsLastGood()
    {
        var s = NewSession();
        s.BeginAttach("http://cdn/video.m3u8");
        Ready(s);

        var first = Probe(s, true);
        var second = Probe(s, true);

        Assert.Single(first, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.DoesNotContain(second, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal("http://cdn/video.m3u8", s.Snapshot.LastGoodUrl);
        Assert.True(s.Snapshot.IsVideoConfirmed);
    }

    [Fact]
    public void FirstFrameBeforeReady_ConfirmsOnReady()
    {
        var s = NewSession();
        s.BeginAttach("http://cdn/video.m3u8");

        Probe(s, true);
        var ready = Ready(s);

        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal("http://cdn/video.m3u8", s.Snapshot.LastGoodUrl);
    }

    [Fact]
    public void ConfirmationOff_ReadyStillReportsWorking_WindowsLinuxApple()
    {
        var s = new PlaybackSessionController(() => _now);
        s.SetHealthyStreamCount(2);
        s.BeginAttach("http://cdn/a.m3u8");

        var ready = Ready(s);

        Assert.Contains(ready, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal("http://cdn/a.m3u8", s.Snapshot.LastGoodUrl);
        Assert.All(Probe(s, false), e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void StaleProbe_IsIgnored()
    {
        var s = NewSession();
        s.BeginAttach("http://cdn/a.m3u8");
        var oldGen = s.Snapshot.AttachGeneration;
        Ready(s);
        s.BeginAttach("http://cdn/b.m3u8", force: true);
        Ready(s);
        _now += TimeSpan.FromSeconds(30);

        var stale = s.Handle(MediaEngineEvent.VideoProbe(oldGen, hasVideo: true));

        Assert.All(stale, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.False(s.Snapshot.IsVideoConfirmed);
    }

    // ---------- Rule 2: revert eligibility ----------

    [Fact]
    public void AudioOnlySwitchTarget_RevertsToLastGoodWithVideo()
    {
        var s = PlayingWithVideo("http://cdn/good.m3u8");
        s.Handle(MediaEngineEvent.UserNext());
        s.BeginAttach("http://cdn/audio.m3u8");
        Ready(s);
        _now += TimeSpan.FromSeconds(9);

        var effects = Probe(s, false);

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood && e.Url == "http://cdn/good.m3u8");
        Assert.Equal("http://cdn/good.m3u8", s.Snapshot.CurrentUrl);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.ReportWorking);
    }

    [Fact]
    public void AudioOnlyStream_IsNeverRevertedTo()
    {
        // The audio-only stream played first (READY only), then the user moved on and the
        // next one failed: there is no last good to go back to, so it advances instead.
        var s = NewSession();
        s.BeginAttach("http://cdn/audio.m3u8");
        Ready(s);
        s.Handle(MediaEngineEvent.UserNext());
        s.BeginAttach("http://cdn/next.m3u8");

        var effects = s.Handle(MediaEngineEvent.Error(s.Snapshot.AttachGeneration, "Response code: 404 for index.m3u8"));

        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RevertToLastGood);
        Assert.DoesNotContain(effects, e => e.Url == "http://cdn/audio.m3u8" && e.Kind == PlaybackEffectKind.Attach);
    }

    [Fact]
    public void LastGoodWhoseFeedFailed_IsNotRevertedTo()
    {
        var s = PlayingWithVideo("http://cdn/good.m3u8");
        // Later the same feed (fresh token) fails dead this cycle.
        s.Handle(MediaEngineEvent.Error(s.Snapshot.AttachGeneration, "Response code: 404 for index.m3u8"));
        Assert.True(s.IsFeedFailedInCycle("http://cdn/good.m3u8"));
        Assert.Null(s.Snapshot.LastGoodUrl);
    }

    // ---------- Rule 3: duplicate of a failed feed ----------

    [Fact]
    public void DuplicateOfFailedFeed_IsSkippedAndRemoved_NotReported()
    {
        var s = NewSession(healthy: 3, untried: () => true);
        s.BeginAttach("http://cdn/a.m3u8");

        var effects = s.NotifyDuplicateOfFailed("Same feed as a failed stream (No video (audio only))");

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.ReportWorking);
    }

    [Fact]
    public async Task AutoAdvance_OntoRowResolvingToFailedFeed_SkipsIt_AndStops()
    {
        var a = Row("A", "http://cdn/premium261/index.m3u8?t=1");
        var b = Row("B", "http://other/b.m3u8");
        var pool = new StreamSwitchingService();
        pool.Initialize("l", "h", "a");
        pool.AddHealthyStream(a);
        pool.AddHealthyStream(b);
        var host = new Host(pool, (_, _) => Task.FromResult<string?>("http://cdn/premium261/index.m3u8?t=2"), () => _now);
        host.AttachCurrent();
        host.Dispatch(MediaEngineEvent.Ready(host.Session.Snapshot.AttachGeneration));

        _now += TimeSpan.FromSeconds(9);
        host.Dispatch(MediaEngineEvent.VideoProbe(host.Session.Snapshot.AttachGeneration, false));
        await Task.Yield();

        Assert.Equal(new[] { "http://cdn/premium261/index.m3u8?t=1" }, host.Attached);
        Assert.Equal(new[] { PlaybackPolicy.NoVideoReason }, host.Failed);
        Assert.Contains(PlaybackPolicy.NoWorkingStreamReason, host.Closed);
        Assert.Equal(PlaybackSessionState.Failed, host.Session.Snapshot.State);
    }

    // ---------- Rule 4: loop guard ----------

    [Fact]
    public void UserNext_WithNothingUntried_StaysPut()
    {
        var s = PlayingWithVideo("http://cdn/a.m3u8", untried: () => false);

        var effects = s.Handle(MediaEngineEvent.UserNext());

        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Contains(effects, e => e.Reason == PlaybackPolicy.NoOtherWorkingStreamText);
    }

    [Fact]
    public void UserClose_AlwaysBreaksOut_EvenWithNothingUntried()
    {
        var s = PlayingWithVideo("http://cdn/a.m3u8", untried: () => false);

        var effects = s.Handle(MediaEngineEvent.UserClose());

        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession);
    }

    [Fact]
    public void AutoAdvance_WithNothingUntried_ClosesWithNoWorkingStreamReason()
    {
        var s = NewSession(healthy: 3, untried: () => false);
        s.BeginAttach("http://cdn/audio.m3u8");
        Ready(s);
        _now += TimeSpan.FromSeconds(9);

        var effects = Probe(s, false);

        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.CloseSession && e.Reason == PlaybackPolicy.NoWorkingStreamReason);
        Assert.Equal("No working stream right now \u2014 retrying in background", PlaybackPolicy.NoWorkingStreamReason);
    }

    [Fact]
    public void InfrastructureFailure_NeverMarksTheStream()
    {
        var marks = new List<string?>();
        var s = new PlaybackSessionController(() => _now);
        s.EnableVideoConfirmation();
        s.SetHealthyStreamCount(3);
        s.ConfigureCycleTracking((url, _, _) => marks.Add(url), null, () => true);
        s.BeginAttach("http://cdn/a.m3u8");

        var effects = s.NotifyFreshResolveInfrastructureUnavailable("Local service unavailable");

        Assert.Empty(marks);
        Assert.DoesNotContain(effects, e => e.Kind == PlaybackEffectKind.RemoveCurrentFromPool);
        Assert.False(s.IsFeedFailedInCycle("http://cdn/a.m3u8"));
    }

    [Fact]
    public void TokenFailure_MarksTheRowButNotTheFeed()
    {
        Assert.False(PlaybackPolicy.FailureMarksFeed("Response code: 403"));
        Assert.True(PlaybackPolicy.FailureMarksFeed(PlaybackPolicy.NoVideoReason));
        Assert.True(PlaybackPolicy.FailureMarksFeed("Response code: 404 for index.m3u8"));
    }

    // ---------- pool ----------

    [Fact]
    public void Pool_Next_SkipsRowsTriedThisCycle_AndKeepsNumbers()
    {
        var (pool, rows) = Pool(4);
        pool.MarkTriedInCycle(rows[1], null, "x");
        pool.MarkTriedInCycle(rows[2], null, "x");

        Assert.True(pool.SwitchToNextStream());

        Assert.Same(rows[3], pool.GetCurrentStream());
        Assert.Equal(4, pool.GetCurrentStreamIndex());
        Assert.Equal(4, pool.GetHealthyStreams().Count);
    }

    [Fact]
    public void Pool_Next_WithEveryOtherRowTried_ReturnsFalse_AndHasNoUntried()
    {
        var (pool, rows) = Pool(3);
        pool.MarkTriedInCycle(rows[1], null, "x");
        pool.MarkTriedInCycle(rows[2], null, "x");

        Assert.False(pool.HasUntriedOther());
        Assert.Null(pool.GetNextHealthyStream());
        Assert.False(pool.SwitchToNextStream());
        Assert.False(pool.SwitchToPreviousStream());
        Assert.Same(rows[0], pool.GetCurrentStream());
    }

    [Fact]
    public void Pool_FailedFeed_SkipsEveryRowOnThatFeed()
    {
        var (pool, rows) = Pool(3);
        pool.MarkTriedInCycle(rows[0], "http://cdn/s1.m3u8?t=9", "No video (audio only)");

        Assert.True(pool.IsSkippedInCycle(rows[1]));
        Assert.Equal("No video (audio only)", pool.FailedFeedReason("http://cdn/s1.m3u8?t=10"));
        Assert.Null(pool.FailedFeedReason("http://cdn/s2.m3u8"));
    }

    [Fact]
    public void Pool_Marks_ExpireAfterTtl_AndClearOnConfirm()
    {
        var (pool, rows) = Pool(3);
        var now = _now;
        pool.SetClock(() => now);
        pool.MarkTriedInCycle(rows[1], null, "x");
        Assert.True(pool.IsSkippedInCycle(rows[1]));

        now += PlaybackPolicy.FailedInCycleTtl + TimeSpan.FromSeconds(1);
        Assert.False(pool.IsSkippedInCycle(rows[1]));

        pool.MarkTriedInCycle(rows[2], null, "x");
        pool.ClearTriedInCycle(rows[2], null);
        Assert.False(pool.IsSkippedInCycle(rows[2]));
    }

    [Fact]
    public void Pool_NoMarks_KeepsOriginalWraparound()
    {
        var (pool, rows) = Pool(3);
        pool.SwitchToStream(2);

        Assert.True(pool.WouldWrapOnNext());
        Assert.True(pool.SwitchToNextStream());
        Assert.Same(rows[0], pool.GetCurrentStream());
    }

    // ---------- helpers ----------

    private (StreamSwitchingService Pool, List<EnrichedStream> Rows) Pool(int count)
    {
        var pool = new StreamSwitchingService();
        pool.SetClock(() => _now);
        pool.Initialize("l", "h", "a");
        var rows = new List<EnrichedStream>();
        for (var i = 0; i < count; i++)
        {
            // Distinct keys on entry, so nothing is merged; row 1 is re-pointed below.
            var url = i == 1 ? "http://cdn/x/s1.m3u8" : $"http://cdn/s{i + 1}.m3u8";
            var row = Row($"C{i + 1}", url);
            pool.AddHealthyStream(row);
            rows.Add(row);
        }

        // Row 1 now resolves to the same feed as row 0 (a later /play).
        rows[1].ResolvedM3U8Url = "http://cdn/s1.m3u8?t=2";
        return (pool, rows);
    }

    private static EnrichedStream Row(string channel, string url) => new()
    {
        Stream = new Stream { Channel = channel, Url = "http://catalog/" + channel },
        ResolvedM3U8Url = url,
        Status = StreamResolutionStatus.Healthy
    };

    private sealed class Host : IPlaybackCommandHost
    {
        private readonly StreamSwitchingService _pool;
        private readonly PlaybackPoolCommandActions _actions;

        public PlaybackSessionController Session { get; }
        public List<string> Attached { get; } = [];
        public List<string?> Failed { get; } = [];
        public List<string> Closed { get; } = [];

        public Host(StreamSwitchingService pool, ResolveFreshPlaybackUrlAsync resolve, Func<DateTimeOffset> clock)
        {
            _pool = pool;
            Session = new PlaybackSessionController(clock);
            Session.EnableVideoConfirmation();
            _actions = new PlaybackPoolCommandActions(
                Session,
                pool,
                resolve,
                (url, cached, force) =>
                {
                    Session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);
                    PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(Session.BeginAttach(url, cached, force)), this);
                },
                cmd => PlaybackCommandExecutor.Apply(cmd, this))
            {
                DelayAsync = static _ => Task.CompletedTask
            };
            Session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);
        }

        public void AttachCurrent()
        {
            var url = _pool.GetCurrentStream()!.ResolvedM3U8Url!;
            PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(Session.BeginAttach(url)), this);
        }

        public void Dispatch(MediaEngineEvent e)
            => PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(Session.Handle(e)), this);

        public void BeginIndexSwitchSuppression() { }
        public void EndIndexSwitchSuppression() { }
        public void ClearCurrentResolvedUrl() => _actions.ClearCurrentResolvedUrl();
        public void RemoveCurrentFromPool() => _actions.RemoveCurrentFromPool();
        public void SyncHealthyStreamCount() => _actions.SyncHealthyStreamCount();
        public void ReportFailed(string? reason) => Failed.Add(reason);
        public void ReportDeclined(string? reason) { }
        public void ReportWorking() { }
        public void MarkEstablished() { }
        public void RaiseBuffering(bool isBuffering) { }
        public void Attach(string url, bool isRevert) => Attached.Add(url);
        public void RevertPoolIndexTo(string url) => _actions.RevertIndexToPlayingUrl(url);
        public void AttachCurrentAfterRemove() => _actions.AttachCurrentFromPoolAsync().GetAwaiter().GetResult();
        public void RetryFreshResolve() => _actions.RetryFreshResolveAsync().GetAwaiter().GetResult();
        public void StopEngine() { }
        public void CloseSession(string reason) => Closed.Add(reason);
        public void SwitchPoolToNext()
        {
            if (_pool.SwitchToNextStream())
                _actions.AttachCurrentFromPoolAsync().GetAwaiter().GetResult();
        }
        public void SwitchPoolToPrevious() => _actions.SwitchPoolToPrevious();
        public void NotifyApplyFailed(Exception exception) => throw exception;
    }
}
