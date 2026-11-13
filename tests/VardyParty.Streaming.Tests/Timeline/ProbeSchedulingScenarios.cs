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
/// lines: script the rows on the clock, then list the outcomes. <see cref="Current"/>
/// cases run as a theory; a new rule not yet in the code goes in <see cref="Gaps"/>
/// with a skipped fact in <see cref="ProbeSchedulingRulesTests"/>.
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

    public static ProbeScenario SeedNeverProbeReady() => ProbeScenario.Named("Chip A playlist-only, chip B never sent; harvest runs to 300s")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A", "B").Chip(9, "A", ChipKind.PlaylistOnly).Done(300))
        .DeadFb("d1");

    /// <summary>LocalService lists nothing for 34s: the seed gives up at 30s and the retry picks up the chips.</summary>
    public static ProbeScenario SeedSilent() => ProbeScenario.Named("/mp silent for 34s, then chips")
        .Mp("mp", MpScript.Headers(8).Chips(34, "A", "B").Chip(35, "A").Chip(36, "B").Done(36.5))
        .DeadFb("d1", "d2");

    /// <summary>Every FB link is dead; the MP page works (first chip ~10s, then ~1s each).</summary>
    public static ProbeScenario AllFbDeadWorkingMp() => ProbeScenario.Named("All FB dead, working MP")
        .Mp("mp", MpScript.Normal(["A", "B", "C"]))
        .DeadFb("d1", "d2", "d3", "d4");

    /// <summary>Every FB link is dead and the MP page's first chip is dead too; the second chip works.</summary>
    public static ProbeScenario AllFbDeadFirstChipDead() => ProbeScenario.Named("All FB dead, MP chip A dead, chip B works")
        .Mp("mp", MpScript.Headers(9).Chips(9, "A", "B", "C")
            .Chip(10, "A", health: StreamHealthStatus.ManifestNotFound)
            .Chip(11, "B")
            .Chip(12, "C")
            .Done(12.5))
        .DeadFb("d1", "d2", "d3", "d4");

    /// <summary>Jon's normal MP (10s, then 1s per chip) with six chips, no healthy FB.</summary>
    public static ProbeScenario FastMp() => ProbeScenario.Named("Fast MP: six chips, 10s then 1s each")
        .Mp("mp", MpScript.Normal(["A", "B", "C", "D", "E", "F"]))
        .DeadFb("d1", "d2", "d3", "d4");

    /// <summary>MP chips that only ever carry a playlist (no rewritten segments).</summary>
    public static ProbeScenario PlaylistOnlyMp() => ProbeScenario.Named("MP chips playlist-only")
        .Mp("mp", MpScript.Normal(["A", "B", "C"], kind: ChipKind.PlaylistOnly))
        .Fb("fbA", Play.HealthyAt(5))
        .DeadFb("fbB", "fbC");

    /// <summary>Every scenario, for the A1-A5 report (<see cref="ProbeSchedulingRulesTests.Acceptance"/>).</summary>
    public static readonly (string Name, Func<ProbeScenario> Scenario)[] All =
    [
        (nameof(CzechiaVSpain), CzechiaVSpain),
        (nameof(ItalyVFrance), ItalyVFrance),
        (nameof(NormalMp), NormalMp),
        (nameof(SlowMpHealthyFb), SlowMpHealthyFb),
        (nameof(MpPendingDeadFbs), MpPendingDeadFbs),
        (nameof(DeadFbsOnly), DeadFbsOnly),
        (nameof(PriorityWindow), PriorityWindow),
        (nameof(CdnUnreachableFirst), CdnUnreachableFirst),
        (nameof(SeedNeverProbeReady), SeedNeverProbeReady),
        (nameof(SeedSilent), SeedSilent),
        (nameof(AllFbDeadWorkingMp), AllFbDeadWorkingMp),
        (nameof(AllFbDeadFirstChipDead), AllFbDeadFirstChipDead),
        (nameof(FastMp), FastMp),
        (nameof(PlaylistOnlyMp), PlaylistOnlyMp),
    ];

    // ---- the table ----

    public static readonly ProbeCase[] Current =
    [
        new("R1 Italy v France: MP probed beside the recommended rows", "R1 R4",
            ItalyVFrance,
            StartOrderAmong(ProbeKind.Play, "f10", "f20", "f30", "f33"),
            StartsBy("mp40", 0.001),
            FirstPlayOn("f10"), FirstPlayBy(9.001),
            ChipsQueued("mp40", 3, by: 9.001)),

        new("R2 two dead FB probed at once (~20s each)", "R2",
            DeadFbsOnly,
            PlaysAtOnceReach(2), SlotsNeverExceed(2),
            RowEnds("d6", StreamResolutionStatus.Failed, 59.9, 60.1),
            NoIdleSlotWhileQueued()),

        new("R3/R6 normal MP: chips become their own rows; seed released at the chip list", "R3 R5 R6 R10",
            NormalMp,
            SeedReturns("mp", 8.9, 9.1),
            ChipsQueued("mp", 5, by: 9.001),
            ChipsVisibleWithin("mp"),
            RowEnds("mp:E", StreamResolutionStatus.Healthy),
            PoolOrder("fbA", "mp", "mp:B", "mp:C", "mp:D", "mp:E"),
            NoIdleSlotWhileQueued()),

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

        new("R11 harvested MP chips are probed inside the priority window", "R11",
            NormalMp,
            FirstPlayOn("fbA"), FirstPlayBy(4.001),
            NoBrowserProbeStartsInPriorityWindow(),
            ChipProbedInPriorityWindow("mp:E"),
            RowEnds("mp:E", StreamResolutionStatus.Healthy, 14.9, 15.1),
            StartsNotBefore("fbC", 34), StartsBy("fbC", 34.001)),

        new("R13 a CDN-unreachable row is Deferred and does not hold first play", "R13",
            CdnUnreachableFirst,
            RowEnds("x", StreamResolutionStatus.Deferred, 5, 5.001),
            FirstPlayOn("y"), FirstPlayBy(9.001)),

        new("R9 Czechia v Spain: first play on a healthy FB while /mp harvests", "R9",
            CzechiaVSpain,
            FirstPlayOn("weakstreams"), FirstPlayBy(13.001),
            FirstPlayWithinOfFirstHealthy()),

        new("R5 Czechia: /mp releases its place once the chip list arrives", "R5",
            CzechiaVSpain,
            SeedReleasesSlotBy("fctv33", 8.401)),

        new("R6 Czechia: all 10 chips queued as rows, none dropped", "R6",
            CzechiaVSpain,
            ChipsQueued("fctv33", 10, by: 8.401),
            ChipsVisibleWithin("fctv33"),
            RowEnds("fctv33", StreamResolutionStatus.Healthy),
            RowEnds("fctv33:TyR", StreamResolutionStatus.Healthy)),

        new("R4 two FB at once while /mp is pending", "R4",
            MpPendingDeadFbs,
            PlaysWhileMpOpenAtLeast(2),
            RowEnds("d6", StreamResolutionStatus.Failed, 59.9, 60.1),
            RowEnds("mp", StreamResolutionStatus.Healthy, 100.9, 101.1),
            NoIdleSlotWhileQueued()),

        new("R7 seed gives up after 30s with nothing listed; the retry queues the chips", "R7 R13",
            SeedSilent,
            SeedReleasesSlotBy("mp", ProbeAcceptance.SeedGiveUpSeconds + 0.001),
            ChipsQueued("mp", 2, by: 37.001),
            RowEnds("mp", StreamResolutionStatus.Healthy, 37.9, 38.1),
            RowEnds("mp:B", StreamResolutionStatus.Healthy, 38.9, 39.1)),

        new("R7 a playlist-only chip is handed over 30s after its playlist line", "R5 R7",
            SeedNeverProbeReady,
            SeedReturns("mp", 7.9, 8.1),
            ChipsQueued("mp", 2, by: 8.001),
            RowEnds("mp", StreamResolutionStatus.Healthy, 39.9, 40.1),
            RowEnds("mp:B", StreamResolutionStatus.Failed, 299.9, 300.1)),

        new("R12 the startup race never waits on /mp (Czechia)", "R12",
            () => CzechiaVSpain().WithStartupRaceDrain(),
            DrainWithin(ProbeAcceptance.DrainSeconds)),

        new("R9 all FB dead: first play on the MP page's first chip", "R6 R9",
            AllFbDeadWorkingMp,
            FirstPlayOn("mp"), FirstPlayBy(11.001),
            FirstPlayWithinOfFirstHealthy(),
            PoolOrder("mp", "mp:B", "mp:C"),
            NoIdleSlotWhileQueued()),

        new("R9 all FB dead, chip A dead: first play on chip B", "R6 R9",
            AllFbDeadFirstChipDead,
            RowEnds("mp", StreamResolutionStatus.Failed, 10.9, 11.1),
            FirstPlayOn("mp:B"), FirstPlayBy(12.001),
            FirstPlayWithinOfFirstHealthy(),
            PoolOrder("mp:B", "mp:C")),

        new("R5 R6 R11 fast MP: every chip probed within a second of arriving", "R5 R6 R10 R11",
            FastMp,
            SeedReturns("mp", 8.9, 9.1),
            FirstPlayOn("mp"), FirstPlayBy(11.001),
            RowEnds("mp:F", StreamResolutionStatus.Healthy, 15.9, 16.1),
            PoolOrder("mp", "mp:B", "mp:C", "mp:D", "mp:E", "mp:F"),
            ChipsVisibleWithin("mp"),
            NoBrowserProbeStartsInPriorityWindow(),
            NoIdleSlotWhileQueued()),

        new("R7 playlist-only chips are probed once the harvest ends", "R6 R7 R11",
            PlaylistOnlyMp,
            FirstPlayOn("fbA"), FirstPlayBy(6.001),
            SeedReturns("mp", 8.9, 9.1),
            RowEnds("mp", StreamResolutionStatus.Healthy, 13.4, 13.6),
            RowEnds("mp:C", StreamResolutionStatus.Healthy, 15.4, 15.6),
            PoolOrder("fbA", "mp", "mp:B", "mp:C"),
            NoBrowserProbeStartsInPriorityWindow()),
    ];

    /// <summary>Rules written down before the code does them. Empty: every rule is CURRENT.</summary>
    public static readonly ProbeCase[] Gaps = [];

    public static ProbeCase Find(string name) =>
        Current.Concat(Gaps).FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"No probe case named '{name}'");
}
