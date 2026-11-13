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

    /// <summary>A3: total seconds a lane place (2 FB, 1 MP) may sit idle while a candidate for it is queued (outside the priority window).</summary>
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

    /// <summary>Order in which rows of one kind (e.g. FB /play) start; the MP lane runs beside them (rule 4).</summary>
    public static Expectation StartOrderAmong(ProbeKind kind, params string[] keys) => new(
        $"{kind} probes start in order {string.Join(", ", keys)}",
        t =>
        {
            var actual = t.Calls.Where(c => c.Kind == kind).Select(c => c.Key).Distinct().Take(keys.Length).ToArray();
            return actual.SequenceEqual(keys) ? null : $"started {string.Join(", ", actual)}";
        });

    /// <summary>Rule 11: no browser probe (/play or a new /mp) starts in the window; harvested chips may.</summary>
    public static Expectation NoBrowserProbeStartsInPriorityWindow() => new(
        "no /play or /mp seed starts inside the 30s playback-priority window",
        t =>
        {
            if (t.PriorityWindow is not { } w)
                return "no priority window happened";
            var inside = t.Calls.Where(c => c.Kind != ProbeKind.MpChip && c.Start > w.From + Eps && c.Start < w.To - Eps).ToList();
            return inside.Count == 0 ? null : $"started inside: {string.Join(", ", inside.Select(c => $"{c.Key}@{Fmt(c.Start)}"))}";
        });

    /// <summary>Rule 11 tweak: a chip the harvest already holds is probed inside the window.</summary>
    public static Expectation ChipProbedInPriorityWindow(string key) => new(
        $"{key} (already harvested) is probed inside the priority window",
        t =>
        {
            if (t.PriorityWindow is not { } w)
                return "no priority window happened";
            return t.FirstCall(key) is { } c && c.Start > w.From - Eps && c.Start < w.To - Eps
                ? null
                : $"started {(t.FirstCall(key) is { } x ? Fmt(x.Start) : "never")}, window {Fmt(w.From)}-{Fmt(w.To)}";
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

    // ---- LocalService load (rules 2-3) ----

    /// <summary>Rule 3: LocalService never runs more than <paramref name="n"/> <c>POST /mp</c> at once.</summary>
    public static Expectation MpPostsAtOnceNeverExceed(int n) => new(
        $"never more than {n} POST /mp open at LocalService",
        t => t.Snapshots.Max(s => s.MpOpen) is var m && m <= n ? null : $"saw {m}");

    /// <summary>Rule 2: <c>/play</c> calls plus open harvests at LocalService.</summary>
    public static Expectation LocalServiceSessionsNeverExceed(int n) => new(
        $"never more than {n} LocalService sessions (/play + /mp)",
        t => t.Snapshots.Max(s => s.LocalServiceSessions) is var m && m <= n
            ? null
            : $"saw {m} at {Fmt(t.Snapshots.First(s => s.LocalServiceSessions == m).At)}");

    /// <summary>
    /// Rule 2: from <paramref name="mpId"/>'s first <c>POST /mp</c> until LocalService lists
    /// its chips (or the seed gives up after 30s), LocalService runs at most
    /// <paramref name="n"/> sessions: the harvest holds a /play slot until then.
    /// </summary>
    public static Expectation SessionsAtMostUntilListed(string mpId, int n = ResolverRaceThrottle.MaxConcurrent) => new(
        $"at most {n} LocalService sessions from {mpId}'s POST /mp until its chips are listed",
        t =>
        {
            if (!t.PostsAt(mpId).Any())
                return $"{mpId} never POSTed";
            var from = t.PostsAt(mpId).Min();
            var until = Math.Min(t.ListedAt(mpId) ?? double.MaxValue, from + ProbeAcceptance.SeedGiveUpSeconds);
            var over = t.Intervals()
                .Where(i => i.Snap.At >= from - Eps && i.Snap.At < until - Eps && i.Snap.LocalServiceSessions > n)
                .ToList();
            return over.Count == 0
                ? null
                : $"{string.Join(", ", over.Take(3).Select(i => $"{i.Snap.LocalServiceSessions} at {Fmt(i.Snap.At)}"))} (listed {(t.ListedAt(mpId) is { } l ? Fmt(l) : "never")})";
        });

    /// <summary>Rule 2: no /mp holds a /play slot from <paramref name="at"/> on (slots in use never exceed /play calls).</summary>
    public static Expectation MpHoldsNoSlotFrom(double at) => new(
        $"no /mp holds a /play slot from t+{at}s on",
        t =>
        {
            var held = t.Snapshots.Where(s => s.At >= at - Eps && s.SlotsInUse > s.PlaysAtLocalService).ToList();
            return held.Count == 0
                ? null
                : $"{string.Join(", ", held.Take(3).Select(s => $"{s.SlotsInUse} slots / {s.PlaysAtLocalService} plays at {Fmt(s.At)}"))}";
        });

    /// <summary>Every slot is free when the run ends (released on every path).</summary>
    public static Expectation NoSlotHeldAtEnd() => new(
        "no /play slot held when the run ends",
        t => t.Snapshots.Count > 0 && t.Snapshots[^1].SlotsInUse == 0 ? null : $"{t.Snapshots.LastOrDefault()?.SlotsInUse} still held");

    /// <summary>Rule 4: once the slot is back, two FB /play run beside the background harvest.</summary>
    public static Expectation PlaysBesideHarvestAtLeast(int n) => new(
        $"{n} /play at LocalService beside an open /mp harvest",
        t => t.Snapshots.Where(s => s.MpOpen > 0).Select(s => s.PlaysAtLocalService).DefaultIfEmpty(0).Max() is var m && m >= n
            ? null
            : $"never more than {m}");

    /// <summary>Rule 6: the page is POSTed exactly <paramref name="n"/> times (no re-POST per chip).</summary>
    public static Expectation MpPostCount(string mpId, int n) => new(
        $"exactly {n} POST /mp for {mpId}",
        t => t.PostsAt(mpId).ToList() is var posts && posts.Count == n
            ? null
            : $"{posts.Count} POSTs at {string.Join(", ", posts.Select(Fmt))}");

    public static Expectation PostStartsBetween(string mpId, double from, double to) => new(
        $"{mpId} POST /mp starts between t+{from}s and t+{to}s",
        t => t.PostsAt(mpId).Select(p => (double?)p).Min() is { } first
            ? first >= from - Eps && first <= to + Eps ? null : $"first POST at {Fmt(first)}"
            : $"{mpId} never POSTed");

    /// <summary>Rule 13: no row of this page (the page row or any of its chips) ends with <paramref name="status"/>.</summary>
    public static Expectation NoRowOfPageEnds(string mpId, StreamResolutionStatus status) => new(
        $"no row of {mpId} ends {status}",
        t =>
        {
            var bad = t.Results.Where(r => (r.Key == mpId || r.Key.StartsWith(mpId + ":", StringComparison.Ordinal)) && r.Status == status).ToList();
            return bad.Count == 0 ? null : string.Join(", ", bad.Select(r => $"{r.Key}@{Fmt(r.At)}"));
        });

    public static Expectation RowsEnd(StreamResolutionStatus status, params string[] keys) => new(
        $"{string.Join(", ", keys)} end {status}",
        t =>
        {
            var bad = keys.Where(k => t.Result(k)?.Status != status)
                .Select(k => t.Result(k) is { } r ? $"{k} {r.Status}@{Fmt(r.At)}" : $"{k} never yielded")
                .ToList();
            return bad.Count == 0 ? null : string.Join(", ", bad);
        });

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
        $"A3 no lane place idle while a candidate is queued (tolerance {toleranceSeconds}s, priority window excluded)",
        t =>
        {
            var idle = t.Intervals()
                .Where(i => i.Snap.IdleWithWork(ResolverRaceThrottle.MaxConcurrent) && !i.Snap.PriorityHold)
                .ToList();
            var total = idle.Sum(i => i.Until - i.Snap.At);
            return total <= toleranceSeconds + Eps
                ? null
                : $"{total:0.###}s idle: {string.Join("; ", idle.Take(5).Select(i => $"{Fmt(i.Snap.At)}-{Fmt(i.Until)} FB {i.Snap.FbActive}+{i.Snap.UnstartedFb} MP {i.Snap.MpActive}+{i.Snap.UnstartedMp}"))}";
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
