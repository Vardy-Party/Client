using System;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class SwitchBackSessionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-14T20:27:53Z");

    [Fact]
    public void Timeline_BetterHeight_SwitchesBackAfterTwoGoodProbes()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);

        var first = session.Next(T0);
        Assert.Equal(SwitchBackAction.Wait, first.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), first.Wait);

        Assert.Equal(SwitchBackAction.Probe, session.Next(T0.AddSeconds(5)).Action);
        var afterFirstGood = session.NoteProbe(playable: true, statusCode: 200, T0.AddSeconds(5));
        Assert.Equal(SwitchBackAction.Wait, afterFirstGood.Action);
        Assert.Equal(TimeSpan.FromSeconds(15), afterFirstGood.Wait);

        Assert.Equal(SwitchBackAction.Probe, session.Next(T0.AddSeconds(20)).Action);
        var back = session.NoteProbe(playable: true, statusCode: 200, T0.AddSeconds(20));
        Assert.Equal(SwitchBackAction.SwitchBack, back.Action);
        Assert.Contains("better height", back.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_SameHeight_WaitsMinDwellBeforeSwitchBack()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 540), T0);
        session.NoteBackupHeight(540, T0);

        session.Next(T0.AddSeconds(5));
        session.NoteProbe(true, 200, T0.AddSeconds(5));
        var second = session.NoteProbe(true, 200, T0.AddSeconds(20));
        Assert.Equal(SwitchBackAction.Wait, second.Action);
        Assert.Contains("dwell", second.Reason, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), second.Wait);

        var back = session.Next(T0.AddSeconds(30));
        Assert.Equal(SwitchBackAction.SwitchBack, back.Action);
        Assert.Contains("user-chosen", back.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UserNext_CancelsSwitchBack()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5));
        session.Cancel("User next");

        var after = session.NoteProbe(true, 200, T0.AddSeconds(20));
        Assert.Equal(SwitchBackAction.Stop, after.Action);
        Assert.Contains("User next", after.Reason, StringComparison.Ordinal);
        Assert.Equal(SwitchBackAction.Stop, session.Next(T0.AddSeconds(20)).Action);
    }

    [Fact]
    public void BadProbe_ResetsConsecutiveGood()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        session.NoteBackupHeight(648, T0);
        session.NoteProbe(true, 200, T0.AddSeconds(5));
        var failed = session.NoteProbe(false, 503, T0.AddSeconds(20));
        Assert.Equal(0, session.ConsecutiveGood);
        Assert.Equal(SwitchBackAction.Wait, failed.Action);

        session.NoteProbe(true, 200, T0.AddSeconds(50));
        var stillWaiting = session.Next(T0.AddSeconds(50));
        Assert.NotEqual(SwitchBackAction.SwitchBack, stillWaiting.Action);
        Assert.Equal(1, session.ConsecutiveGood);
    }

    [Fact]
    public void AuthFailure_StopsWithoutSwitchBack()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(height: 1080), T0);
        var stop = session.NoteProbe(false, 403, T0.AddSeconds(5));
        Assert.Equal(SwitchBackAction.Stop, stop.Action);
        Assert.True(session.IsCancelled);
    }

    [Fact]
    public void IsSwitchBackEnabled_DefaultAndExplicit()
    {
        Assert.True(PlaylistReloadStrategies.IsSwitchBackEnabled(null));
        Assert.True(PlaylistReloadStrategies.IsSwitchBackEnabled(PlaylistReloadStrategyNames.RetrySameUrlThenSwitchBack));
        Assert.True(PlaylistReloadStrategies.IsSwitchBackEnabled("TemporarySwitch"));
        Assert.False(PlaylistReloadStrategies.IsSwitchBackEnabled(PlaylistReloadStrategyNames.RetrySameUrl));
    }

    [Fact]
    public void Commands_ArmOnCdnUnreachableAdvance_AndCancelOnUserNext()
    {
        var session = new SwitchBackSession();
        var logs = new System.Collections.Generic.List<string>();
        var candidate = Candidate(1080);
        var moveOn = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: $"{PlaybackCloseReasons.CdnUnreachable}: network connection failed")
        ]);

        SwitchBackCommands.Apply(session, enabled: true, moveOn, candidate, T0, logs.Add);
        Assert.True(session.IsArmed);
        Assert.Contains(logs, l => l.Contains("[switch-back] watching", System.StringComparison.Ordinal));

        var userNext = PlaybackCommand.FromEffects([
            new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: "User next")
        ]);
        SwitchBackCommands.Apply(session, enabled: true, userNext, candidate, T0.AddSeconds(1), logs.Add);
        Assert.True(session.IsCancelled);
        Assert.Contains(logs, l => l.Contains("[switch-back] cancelled", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loop_TwoGoodProbes_SwitchesBackAndLogs()
    {
        var session = new SwitchBackSession();
        session.Arm(Candidate(1080), T0);
        session.NoteBackupHeight(648, T0);
        var now = T0;
        var probes = 0;
        var switched = 0;
        var logs = new System.Collections.Generic.List<string>();
        using var cts = new System.Threading.CancellationTokenSource();

        await SwitchBackLoop.RunAsync(
            session,
            (_, _) =>
            {
                probes++;
                return System.Threading.Tasks.Task.FromResult((true, 200));
            },
            (_, _, _) =>
            {
                switched++;
                cts.Cancel();
                return System.Threading.Tasks.Task.CompletedTask;
            },
            (delay, ct) =>
            {
                now += delay;
                ct.ThrowIfCancellationRequested();
                return System.Threading.Tasks.Task.CompletedTask;
            },
            () => now,
            logs.Add,
            cts.Token);

        Assert.Equal(2, probes);
        Assert.Equal(1, switched);
        Assert.Contains(logs, l => l.StartsWith("[switch-back] returning", System.StringComparison.Ordinal));
    }

    private static SwitchBackCandidate Candidate(int height) =>
        new("https://cdn.example/wawes.m3u8", "Match Wawes", "wawes", height, null, "https://matchli.st/");
}
