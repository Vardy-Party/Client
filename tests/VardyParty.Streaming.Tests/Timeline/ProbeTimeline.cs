using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VardyParty.Kernel;

namespace VardyParty.Streaming.Tests.Timeline;

internal enum ProbeKind
{
    Play,
    MpSeed,
    MpChip
}

/// <summary>One resolver call into LocalLanPlayService (includes any wait for a throttle slot).</summary>
internal sealed class ProbeCall
{
    public required string Key { get; init; }
    public required string RowId { get; init; }
    public required ProbeKind Kind { get; init; }
    public required double Start { get; init; }
    public double? End { get; set; }
    public string? Outcome { get; set; }

    public bool OpenAt(double t) => Start <= t && (End is null || End > t);
}

/// <summary>State between two milestones: from <see cref="At"/> until the next snapshot.</summary>
internal sealed record Snapshot(
    double At,
    int SlotsInUse,
    int PlaysAtLocalService,
    int MpOpen,
    int HealthOpen,
    int UnstartedFb,
    int UnstartedMp,
    int FbActive,
    int MpActive,
    bool PriorityHold,
    IReadOnlyList<string> InFlight,
    bool HeadStartHold = false)
{
    public int Unstarted => UnstartedFb + UnstartedMp;

    /// <summary>Chromium sessions at LocalService: <c>/play</c> calls plus open <c>/mp</c> harvests.</summary>
    public int LocalServiceSessions => PlaysAtLocalService + MpOpen;

    /// <summary>
    /// A lane is idle with work queued: the FB window has a free place and an FB row
    /// waits, or the MP lane is empty and an MP row waits while no harvest runs at
    /// LocalService (rules 2-4, 7: queued chips of a running harvest wait on it by design).
    /// </summary>
    public bool IdleWithWork(int fbWindow) =>
        (FbActive < fbWindow && UnstartedFb > 0) || (MpActive == 0 && UnstartedMp > 0 && MpOpen == 0);
}

internal sealed record RowResult(string Key, double At, StreamResolutionStatus Status, string? Error);

internal sealed record ChipsQueued(string RowId, double At, int Count);

/// <summary>Everything a scenario run observed, in scenario seconds (t+).</summary>
internal sealed class ProbeTimeline
{
    public required ProbeScenario Scenario { get; init; }
    public List<ProbeCall> Calls { get; } = new();
    public List<Snapshot> Snapshots { get; } = new();
    public List<RowResult> Results { get; } = new();
    public List<ChipsQueued> ChipsQueued { get; } = new();
    public List<(double At, string RowId, string What)> LocalServiceEvents { get; } = new();
    public List<(double At, string Key, StreamHealthStatus Status)> HealthVerdicts { get; } = new();
    public List<string> Pool { get; } = new();
    public List<(double At, string Line)> Log { get; } = new();
    public (double At, string Key)? FirstPlay { get; set; }

    /// <summary>How first play opened (<see cref="FirstPlayOpenStrategy.StartKind"/>).</summary>
    public FirstPlayOpenStrategy.StartKind? FirstPlayKind { get; set; }

    /// <summary>The fresh start <c>/play</c> (only when the probed link could not be reused).</summary>
    public (double From, double To)? FreshStartPlay { get; set; }

    /// <summary>The head start (rule 1): from, until, the High (top-tier) keys and the Medium keys.</summary>
    public (double From, double To, IReadOnlyList<string> TopTier, IReadOnlyList<string> Medium)? HeadStart { get; set; }

    /// <summary>
    /// Probe keys of High and Medium rows, including the first-chip key a tier MP page's
    /// seed is re-queued under (its chip call is "page:chip", its result "page").
    /// </summary>
    public HashSet<string> TierCallKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The playback-priority window (rule 11): from first play for 30s.</summary>
    public (double From, double To)? PriorityWindow { get; set; }

    /// <summary>When the window actually paused finding (a backup needed warming).</summary>
    public List<(double From, double To)> PriorityHolds { get; } = new();

    /// <summary>The first backup (the next channel after the playing one) and when it was chosen.</summary>
    public (double At, string Key)? Backup { get; set; }

    /// <summary>When that backup was ready to switch to (needs no warm, or its warm <c>/play</c> answered).</summary>
    public double? BackupReadyAt { get; set; }

    /// <summary>Warm <c>/play</c>s for backups (prewarmer and the fresh start's partner): key, from, to.</summary>
    public List<(string Key, double From, double To)> Warms { get; } = new();

    public double EndedAt { get; set; }

    /// <summary>Harness settles (one per milestone) and the wall-clock they took, so slow or flaky runs show up.</summary>
    public int SettleCount { get; set; }

    public double SettleTotalMs { get; set; }

    public double SettleMaxMs { get; set; }

    /// <summary>When LocalService first listed chips (or sent a chip) for <paramref name="rowId"/>'s Nth <c>POST /mp</c>.</summary>
    public double? ListedAt(string rowId) =>
        LocalServiceEvents
            .Where(e => e.RowId == rowId && (e.What.StartsWith("/mp chips", StringComparison.Ordinal) || e.What.StartsWith("/mp chip ", StringComparison.Ordinal)))
            .Select(e => (double?)e.At)
            .Min();

    public IEnumerable<double> PostsAt(string rowId) =>
        LocalServiceEvents.Where(e => e.RowId == rowId && e.What.StartsWith("POST /mp", StringComparison.Ordinal)).Select(e => e.At);

    public RowResult? Result(string key) => Results.FirstOrDefault(r => r.Key == key);

    public ProbeCall? FirstCall(string key) => Calls.FirstOrDefault(c => c.Key == key);

    public IEnumerable<(Snapshot Snap, double Until)> Intervals()
    {
        for (var i = 0; i < Snapshots.Count; i++)
        {
            var until = i + 1 < Snapshots.Count ? Snapshots[i + 1].At : EndedAt;
            if (until > Snapshots[i].At)
                yield return (Snapshots[i], until);
        }
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scenario: {Scenario.Name}");
        sb.AppendLine($"First play: {(FirstPlay is { } fp ? $"{Fmt(fp.At)} on {fp.Key} ({FirstPlayKind})" : "none")}");
        if (HeadStart is { } hs)
            sb.AppendLine($"Head start: {Fmt(hs.From)}-{Fmt(hs.To)} High {string.Join(", ", hs.TopTier)}; Medium {string.Join(", ", hs.Medium)}");
        if (FreshStartPlay is { } fsp)
            sb.AppendLine($"Fresh start /play: {Fmt(fsp.From)}-{Fmt(fsp.To)}");
        sb.AppendLine($"Backup: {(Backup is { } b ? $"{b.Key} chosen {Fmt(b.At)}, ready {(BackupReadyAt is { } r ? Fmt(r) : "never")}" : "none")}");
        sb.AppendLine($"Priority holds: {(PriorityHolds.Count == 0 ? "none" : string.Join(", ", PriorityHolds.Select(h => $"{Fmt(h.From)}-{Fmt(h.To)}")))}");
        sb.AppendLine($"Pool (channel order): {string.Join(", ", Pool.Select((k, i) => $"#{i + 1} {k}"))}");
        sb.AppendLine($"Harness: {SettleCount} settles, {SettleTotalMs:0} ms total, slowest {SettleMaxMs:0} ms");
        sb.AppendLine("Milestones (play slots / plays at LS / mp open / health / FB active+queued / MP active+queued / hold | in flight):");
        foreach (var s in Snapshots)
            sb.AppendLine($"  {Fmt(s.At)}  {s.SlotsInUse}/{ResolverRaceThrottle.MaxConcurrent}  {s.PlaysAtLocalService}  {s.MpOpen}  {s.HealthOpen}  FB {s.FbActive}+{s.UnstartedFb}  MP {s.MpActive}+{s.UnstartedMp}  {(s.PriorityHold ? "HOLD" : s.HeadStartHold ? "HEAD" : "-")} | {string.Join(", ", s.InFlight)}");
        sb.AppendLine("Events:");
        foreach (var (at, line) in Log.OrderBy(l => l.At))
            sb.AppendLine($"  {Fmt(at)}  {line}");
        return sb.ToString();
    }

    public static string Fmt(double t) => "t+" + t.ToString("000.000", CultureInfo.InvariantCulture) + "s";
}
