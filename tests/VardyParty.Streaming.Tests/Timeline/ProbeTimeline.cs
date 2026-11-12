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
    int Unstarted,
    bool PriorityHold,
    IReadOnlyList<string> InFlight);

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
    public (double From, double To)? PriorityWindow { get; set; }
    public double EndedAt { get; set; }

    /// <summary>When the startup race's DrainOthersAsync returned (see <see cref="ProbeScenario.WithStartupRaceDrain"/>).</summary>
    public double? DrainedAt { get; set; }

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
        sb.AppendLine($"First play: {(FirstPlay is { } fp ? $"{Fmt(fp.At)} on {fp.Key}" : "none")}");
        sb.AppendLine($"Pool (channel order): {string.Join(", ", Pool.Select((k, i) => $"#{i + 1} {k}"))}");
        sb.AppendLine("Milestones (slots / plays at LS / mp open / health / unstarted / hold | in flight):");
        foreach (var s in Snapshots)
            sb.AppendLine($"  {Fmt(s.At)}  {s.SlotsInUse}/{ResolverRaceThrottle.MaxConcurrent}  {s.PlaysAtLocalService}  {s.MpOpen}  {s.HealthOpen}  {s.Unstarted}  {(s.PriorityHold ? "HOLD" : "-")} | {string.Join(", ", s.InFlight)}");
        sb.AppendLine("Events:");
        foreach (var (at, line) in Log.OrderBy(l => l.At))
            sb.AppendLine($"  {Fmt(at)}  {line}");
        return sb.ToString();
    }

    public static string Fmt(double t) => "t+" + t.ToString("000.000", CultureInfo.InvariantCulture) + "s";
}
