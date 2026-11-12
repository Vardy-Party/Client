using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using static VardyParty.Streaming.Tests.Timeline.ProbeTimeline;

namespace VardyParty.Streaming.Tests.Timeline;

/// <summary>A measurable timeline outcome. <see cref="Check"/> returns null when it holds, else why not.</summary>
internal sealed record Expectation(string Description, Func<ProbeTimeline, string?> Check)
{
    public override string ToString() => Description;
}

/// <summary>
/// Proposed acceptance numbers (docs/PROBE_SCHEDULING_RULES.md "Acceptance").
/// PROPOSED - Jon to set.
/// </summary>
internal static class ProbeAcceptance
{
    /// <summary>A1: first play within this many seconds of the first healthy verdict.</summary>
    public const double FirstPlayAfterHealthySeconds = 2;

    /// <summary>A2: MP chips queued as rows within this many seconds of LocalService listing them.</summary>
    public const double ChipsVisibleSeconds = 2;

    /// <summary>A3: total seconds a slot may sit idle while a candidate is queued (outside the priority window).</summary>
    public const double IdleSlotToleranceSeconds = 1;

    /// <summary>A4: an /mp seed gives up (and releases its slot) this many seconds after the POST.</summary>
    public const double SeedGiveUpSeconds = 30;

    /// <summary>A5: a startup race drains find-phase probes within one dead FB (~20s) plus this margin.</summary>
    public const double DrainSeconds = 21;
}

internal static class Expect
{
    private const double Eps = 0.001;

    public static Expectation FirstPlayBy(double by) => new(
        $"first play by t+{by}s",
        t => t.FirstPlay is { } fp && fp.At <= by + Eps ? null : $"first play was {(t.FirstPlay is { } f ? Fmt(f.At) : "never")}");

    public static Expectation FirstPlayOn(string key) => new(
        $"first play on {key}",
        t => t.FirstPlay?.Key == key ? null : $"first play was on {t.FirstPlay?.Key ?? "nothing"}");

    public static Expectation RowEnds(string key, StreamResolutionStatus status, double from = 0, double to = double.MaxValue) => new(
        $"{key} ends {status}" + (to < double.MaxValue ? $" between t+{from}s and t+{to}s" : ""),
        t => t.Result(key) is { } r
            ? r.Status == status && r.At >= from - Eps && r.At <= to + Eps
                ? null
                : $"{key} ended {r.Status} at {Fmt(r.At)}"
            : $"{key} never yielded");

    public static Expectation ChipsQueued(string mpId, int count, double by = double.MaxValue) => new(
        $"{count} chips of {mpId} queued as their own rows" + (by < double.MaxValue ? $" by t+{by}s" : ""),
        t =>
        {
            var queued = t.ChipsQueued.Where(c => c.RowId == mpId && c.At <= by + Eps).Sum(c => c.Count);
            return queued >= count ? null : $"{queued} chips queued by then (events: {Describe(t.ChipsQueued.Where(c => c.RowId == mpId))})";
        });

    public static Expectation NoChipsQueued(string mpId) => new(
        $"no chips of {mpId} queued (documents today's drop)",
        t => t.ChipsQueued.Any(c => c.RowId == mpId && c.Count > 0) ? $"chips queued: {Describe(t.ChipsQueued.Where(c => c.RowId == mpId))}" : null);

    /// <summary>The seed call (and so its throttle slot and the MP single-flight place) ends in this window.</summary>
    public static Expectation SeedReturns(string mpId, double from, double to) => new(
        $"{mpId} /mp seed returns (slot released) between t+{from}s and t+{to}s",
        t => t.FirstCall(mpId) is { Kind: ProbeKind.MpSeed } c
            ? c.End is { } end && end >= from - Eps && end <= to + Eps ? null : $"seed returned {(c.End is { } e ? Fmt(e) : "never")}"
            : $"{mpId} seed never started");

    public static Expectation SeedReleasesSlotBy(string mpId, double by) => SeedReturns(mpId, 0, by);

    public static Expectation PlaysWhileMpOpenAtMost(int n) => new(
        $"at most {n} /play at LocalService while an MP probe is open",
        t => MaxPlaysWhileMp(t) is var m && m <= n ? null : $"saw {m}");

    public static Expectation PlaysWhileMpOpenAtLeast(int n) => new(
        $"at least {n} /play at LocalService at once while an MP probe is open",
        t => MaxPlaysWhileMp(t) is var m && m >= n ? null : $"never more than {m}");

    public static Expectation PlaysAtOnceReach(int n) => new(
        $"{n} /play at LocalService at once",
        t => t.Snapshots.Max(s => s.PlaysAtLocalService) is var m && m >= n ? null : $"never more than {m}");

    public static Expectation SlotsNeverExceed(int n) => new(
        $"never more than {n} LocalService slots in use",
        t => t.Snapshots.Max(s => s.SlotsInUse) is var m && m <= n ? null : $"saw {m}");

    public static Expectation StartOrder(params string[] keys) => new(
        $"probes start in order {string.Join(", ", keys)}",
        t =>
        {
            var actual = t.Calls.Select(c => c.Key).Distinct().Take(keys.Length).ToArray();
            return actual.SequenceEqual(keys) ? null : $"started {string.Join(", ", actual)}";
        });

    public static Expectation StartsBy(string key, double by) => new(
        $"{key} probe starts by t+{by}s",
        t => t.FirstCall(key) is { } c && c.Start <= by + Eps ? null : $"started {(t.FirstCall(key) is { } x ? Fmt(x.Start) : "never")}");

    public static Expectation StartsNotBefore(string key, double notBefore) => new(
        $"{key} probe does not start before t+{notBefore}s",
        t => t.FirstCall(key) is { } c
            ? c.Start >= notBefore - Eps ? null : $"started {Fmt(c.Start)}"
            : $"{key} never started");

    public static Expectation PoolOrder(params string[] keys) => new(
        $"pool (channel numbers) starts {string.Join(", ", keys.Select((k, i) => $"#{i + 1} {k}"))}",
        t => t.Pool.Take(keys.Length).SequenceEqual(keys) ? null : $"pool is {string.Join(", ", t.Pool)}");

    public static Expectation NoProbeStartsInPriorityWindow() => new(
        "no new probe starts inside the 30s playback-priority window",
        t =>
        {
            if (t.PriorityWindow is not { } w)
                return "no priority window happened";
            var inside = t.Calls.Where(c => c.Start > w.From + Eps && c.Start < w.To - Eps).ToList();
            return inside.Count == 0 ? null : $"started inside: {string.Join(", ", inside.Select(c => $"{c.Key}@{Fmt(c.Start)}"))}";
        });

    public static Expectation DrainWithin(double seconds) => new(
        $"startup race drain finishes within {seconds}s of first play",
        t => t.FirstPlay is not { } fp
            ? "no first play"
            : t.DrainedAt is { } d && d - fp.At <= seconds + Eps ? null : $"drained {(t.DrainedAt is { } x ? Fmt(x) : "never")}");

    public static Expectation DrainNotBefore(double at) => new(
        $"startup race drain waits until t+{at}s (documents today)",
        t => t.DrainedAt is { } d && d >= at - Eps ? null : $"drained {(t.DrainedAt is { } x ? Fmt(x) : "never")}");

    // ---- acceptance (numbers PROPOSED, see ProbeAcceptance) ----

    public static Expectation FirstPlayWithinOfFirstHealthy(double seconds = ProbeAcceptance.FirstPlayAfterHealthySeconds) => new(
        $"A1 first play within {seconds}s of the first healthy verdict",
        t =>
        {
            var healthy = t.HealthVerdicts.Where(h => h.Status == StreamHealthStatus.Healthy).Select(h => (double?)h.At).Min();
            if (healthy is null)
                return "no healthy verdict";
            return t.FirstPlay is { } fp && fp.At - healthy <= seconds + Eps
                ? null
                : $"first healthy {Fmt(healthy.Value)}, first play {(t.FirstPlay is { } f ? Fmt(f.At) : "never")}";
        });

    public static Expectation ChipsVisibleWithin(string mpId, double seconds = ProbeAcceptance.ChipsVisibleSeconds) => new(
        $"A2 {mpId} chips queued within {seconds}s of LocalService listing them",
        t =>
        {
            var listed = t.LocalServiceEvents.Where(e => e.RowId == mpId && e.What.StartsWith("/mp chips", StringComparison.Ordinal))
                .Select(e => (double?)e.At).Min();
            if (listed is null)
                return "LocalService never listed chips";
            var queued = t.ChipsQueued.Where(c => c.RowId == mpId && c.Count > 0).Select(c => (double?)c.At).Min();
            return queued is { } q && q - listed <= seconds + Eps
                ? null
                : $"listed {Fmt(listed.Value)}, queued {(queued is { } x ? Fmt(x) : "never")}";
        });

    public static Expectation NoIdleSlotWhileQueued(double toleranceSeconds = ProbeAcceptance.IdleSlotToleranceSeconds) => new(
        $"A3 no slot idle while a candidate is queued (tolerance {toleranceSeconds}s, priority window excluded)",
        t =>
        {
            var idle = t.Intervals()
                .Where(i => i.Snap.SlotsInUse < ResolverRaceThrottle.MaxConcurrent && i.Snap.Unstarted > 0 && !i.Snap.PriorityHold)
                .ToList();
            var total = idle.Sum(i => i.Until - i.Snap.At);
            return total <= toleranceSeconds + Eps
                ? null
                : $"{total:0.###}s idle: {string.Join("; ", idle.Take(5).Select(i => $"{Fmt(i.Snap.At)}-{Fmt(i.Until)} slots {i.Snap.SlotsInUse} queued {i.Snap.Unstarted}"))}";
        });

    private static int MaxPlaysWhileMp(ProbeTimeline t) =>
        t.Intervals()
            .Where(i => t.Calls.Any(c => c.Kind != ProbeKind.Play && c.OpenAt(i.Snap.At)))
            .Select(i => i.Snap.PlaysAtLocalService)
            .DefaultIfEmpty(0)
            .Max();

    private static string Describe(IEnumerable<ChipsQueued> events) =>
        string.Join(", ", events.Select(e => $"{e.Count}@{Fmt(e.At)}")) is { Length: > 0 } s ? s : "none";
}
