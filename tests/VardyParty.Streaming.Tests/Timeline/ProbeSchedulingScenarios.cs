using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using static VardyParty.Streaming.Tests.Timeline.Expect;

namespace VardyParty.Streaming.Tests.Timeline;

/// <summary>One row of the scenario table: a realistic case and the rule outcomes it must show.</summary>
internal sealed record ProbeCase(string Name, string Rules, Func<ProbeScenario> Scenario, params Expectation[] Expect)
{
    public override string ToString() => Name;
}

/// <summary>
/// The scenario table for docs/PROBE_SCHEDULING_RULES.md. Adding a case is a few
/// lines: script the rows on the clock, then list the outcomes. CURRENT cases run;
/// GAP cases are listed by <see cref="Gaps"/> and run from skipped facts in
/// <see cref="ProbeSchedulingRulesTests"/> until the code catches up.
/// Times are seconds after the first probe (t+0 = 20:12:25 UK time, 12 Nov 2026).
/// </summary>
internal static class ProbeSchedulingScenarios
{
    // ---- realistic cases ----

    private static readonly string[] CzechiaChips =
        ["VUS", "Fubo US", "Movistar2", "SKY IT", "LP FR", "TD Arabic", "Sport PT", "Mo TH", "TV VN", "TyR"];

    /// <summary>
    /// Czechia v Spain, Windows, 12 Nov 2026 (20:12:25-20:14:25 UK time). /mp headers at
    /// 8.4s, ten chips listed, ~10s per chip, none probe-ready, done at ~111s. FB rows as
    /// in the log: weakstreams / Hesgoal / SportsRope healthy, CrackStreams and others dead.
    /// </summary>
    public static ProbeScenario CzechiaVSpain() => ProbeScenario.Named("Czechia v Spain (Windows 12 Nov)")
        .Mp("fctv33", MpScript.SlowNoProbeReadySeed(CzechiaChips))
        .Fb("weakstreams", Play.HealthyAt(12))
        .Fb("crackstreams", Play.Dead())
        .Fb("hesgoal", Play.HealthyAt(15))
        .Fb("sportsrope", Play.HealthyAt(10))
        .DeadFb("fb-dead-1", "fb-dead-2");

    /// <summary>Italy v France: 54 rows, four recommended, the MP page buried at catalog row 41.</summary>
    public static ProbeScenario ItalyVFrance()
    {
        var s = ProbeScenario.Named("Italy v France (54 rows, MP buried)").StopAt(120);
        for (var i = 0; i < 54; i++)
        {
            var id = $"f{i:00}";
            if (i == 40)
                s.Mp("mp40", MpScript.Normal(["Chip A", "Chip B", "Chip C"]));
            else if (i == 10)
                s.Fb(id, Play.HealthyAt(8));
            else if (i == 20)
                s.Fb(id, Play.HealthyAt(9));
            else
                s.Fb(id, Play.Dead());
        }

        return s.Recommend("f10").Recommend("f20")
            .Recommend("f30", RecommendationConfidence.Medium)
            .Recommend("f33", RecommendationConfidence.Low);
    }

    /// <summary>A normal /mp (Jon: ~10s for the first chip, ~1s per extra chip) beside FB rows.</summary>
    public static ProbeScenario NormalMp() => ProbeScenario.Named("Normal MP: first chip ~10s, then ~1s each")
        .Mp("mp", MpScript.Normal(["A", "B", "C", "D", "E"]))
        .Fb("fbA", Play.HealthyAt(3))
        .DeadFb("fbB", "fbC");

    public static ProbeScenario SlowMpHealthyFb() => ProbeScenario.Named("Slow MP at try-order 0, healthy FB at 1")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A").Chip(60, "A").Done(61))
        .Fb("south", Play.HealthyAt(5))
        .DeadFb("d1", "d2");

    public static ProbeScenario MpPendingDeadFbs() => ProbeScenario.Named("/mp pending 100s, six dead FB")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A").Chip(100, "A").Done(101))
        .DeadFb("d1", "d2", "d3", "d4", "d5", "d6");

    public static ProbeScenario DeadFbsOnly() => ProbeScenario.Named("Six dead FB, no MP")
        .DeadFb("d1", "d2", "d3", "d4", "d5", "d6");

    public static ProbeScenario PriorityWindow() => ProbeScenario.Named("First play then the 30s priority window")
        .Fb("fbA", Play.HealthyAt(5))
        .DeadFb("d1", "d2", "d3", "d4");

    public static ProbeScenario CdnUnreachableFirst() => ProbeScenario.Named("CDN-unreachable row ahead of a healthy row")
        .Fb("x", Play.CdnUnreachableAt(5))
        .Fb("y", Play.HealthyAt(8))
        .DeadFb("z");

    public static ProbeScenario SeedNeverProbeReady() => ProbeScenario.Named("Seed never probe-ready; harvest runs to 300s")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A", "B").Chip(9, "A", ChipKind.PlaylistOnly).Done(300))
        .DeadFb("d1");

    // ---- the table ----

    public static readonly ProbeCase[] Current =
    [
        new("R1 Italy v France: MP probed right after the recommended rows", "R1",
            ItalyVFrance,
            StartOrder("f10", "f20", "f30", "f33", "mp40"),
            StartsBy("mp40", 60),
            FirstPlayOn("f10"), FirstPlayBy(9.001),
            ChipsQueued("mp40", 2, by: 71)),

        new("R2 two dead FB probed at once (~20s each)", "R2",
            DeadFbsOnly,
            PlaysAtOnceReach(2), SlotsNeverExceed(2),
            RowEnds("d6", StreamResolutionStatus.Failed, 59.9, 60.1),
            NoIdleSlotWhileQueued()),

        new("R3/R6 normal MP: chips become their own rows; slot freed at the first chip", "R3 R6 R10",
            NormalMp,
            SeedReturns("mp", 9.9, 10.1),
            ChipsQueued("mp", 4, by: 11.001),
            ChipsVisibleWithin("mp"),
            RowEnds("mp:E", StreamResolutionStatus.Healthy),
            PoolOrder("fbA", "mp", "mp:B", "mp:C", "mp:D", "mp:E")),

        new("R9 slow MP never holds a healthy FB first play", "R9",
            SlowMpHealthyFb,
            FirstPlayOn("south"), FirstPlayBy(6.001),
            FirstPlayWithinOfFirstHealthy(),
            RowEnds("mp", StreamResolutionStatus.Healthy, 60.9, 61.1),
            PoolOrder("south", "mp")),

        new("R11 after first play no new probe starts for 30s", "R11",
            PriorityWindow,
            FirstPlayBy(6.001),
            NoProbeStartsInPriorityWindow(),
            StartsNotBefore("d2", 36), StartsBy("d2", 36.001)),

        new("R13 a CDN-unreachable row is Deferred and does not hold first play", "R13",
            CdnUnreachableFirst,
            RowEnds("x", StreamResolutionStatus.Deferred, 5, 5.001),
            FirstPlayOn("y"), FirstPlayBy(9.001)),

        new("R9 Czechia v Spain: first play on a healthy FB while /mp harvests", "R9",
            CzechiaVSpain,
            FirstPlayOn("weakstreams"), FirstPlayBy(13.001)),

        // TODAY cases pin what the code does now so the harness proves it reproduces
        // the field logs. Each flips (fails) when its GAP rule is implemented: then
        // delete it and un-skip the GAP fact.
        new("TODAY Czechia v Spain: seed waits the whole harvest, Failed, 10 chips dropped", "R5 R6 (gap)",
            CzechiaVSpain,
            SeedReturns("fctv33", 110.9, 111.1),
            RowEnds("fctv33", StreamResolutionStatus.Failed, 110.9, 111.1),
            NoChipsQueued("fctv33"),
            PlaysWhileMpOpenAtMost(1)),

        new("TODAY one FB at a time while /mp is pending", "R4 (gap)",
            MpPendingDeadFbs,
            PlaysWhileMpOpenAtMost(1)),

        new("TODAY a seed with no probe-ready chip waits until done (300s)", "R7 (gap)",
            SeedNeverProbeReady,
            SeedReturns("mp", 299.9, 300.1),
            RowEnds("mp", StreamResolutionStatus.Failed)),

        new("TODAY the startup race drain waits on /mp (Czechia)", "R12 (gap)",
            () => CzechiaVSpain().WithStartupRaceDrain(),
            DrainNotBefore(110.9)),
    ];

    public static readonly ProbeCase[] Gaps =
    [
        new("GAP R5 Czechia: /mp releases its slot once the chip list arrives", "R5",
            CzechiaVSpain,
            SeedReleasesSlotBy("fctv33", 9.5)),

        new("GAP R6 Czechia: all 10 chips queued as rows, none dropped", "R6",
            CzechiaVSpain,
            ChipsQueued("fctv33", 10),
            ChipsVisibleWithin("fctv33")),

        new("GAP R4 two FB at once while /mp is pending", "R4",
            MpPendingDeadFbs,
            PlaysWhileMpOpenAtLeast(2)),

        new("GAP R7 seed gives up after 30s with no probe-ready chip", "R7",
            SeedNeverProbeReady,
            SeedReleasesSlotBy("mp", ProbeAcceptance.SeedGiveUpSeconds + 0.001)),

        new("GAP R12 the startup race never waits on /mp (Czechia)", "R12",
            () => CzechiaVSpain().WithStartupRaceDrain(),
            DrainWithin(ProbeAcceptance.DrainSeconds)),
    ];

    public static ProbeCase Find(string name) =>
        Current.Concat(Gaps).FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"No probe case named '{name}'");
}
