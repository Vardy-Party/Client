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

    private static readonly string[] TenChips =
        ["Alpha", "Bravo US", "Charlie2", "Delta IT", "Echo FR", "Foxtrot Arabic", "Golf PT", "Hotel TH", "India VN", "Juliet"];

    /// <summary>
    /// Home United v Away City: a slow MP page from a real Windows run (12 Nov 2026,
    /// provenance in docs/PROBE_SCHEDULING_RULES.md). /mp headers at 8.4s, ten chips
    /// listed, ~10s per chip, none probe-ready, done at ~111s. Three FB rows healthy,
    /// the others dead.
    /// </summary>
    public static ProbeScenario SlowTenChipPage() => ProbeScenario.Named("Home United v Away City (10 slow chips, 111s harvest)")
        .Mp("mp-north", MpScript.SlowNoProbeReadySeed(TenChips))
        .Fb("fb-north", Play.HealthyAt(12))
        .Fb("fb-east", Play.Dead())
        .Fb("fb-south", Play.HealthyAt(15))
        .Fb("fb-west", Play.HealthyAt(10))
        .DeadFb("fb-dead-1", "fb-dead-2");

    /// <summary>Northern Rovers v Southern Athletic: 54 rows, four recommended, the MP page buried at catalog row 41.</summary>
    public static ProbeScenario BuriedMp54Rows()
    {
        var s = ProbeScenario.Named("Northern Rovers v Southern Athletic (54 rows, MP buried)").StopAt(120);
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

    /// <summary>A listed chip LocalService sends 50s after the chip list, with no other line in between.</summary>
    public static ProbeScenario ChipLateAfterListing() => ProbeScenario.Named("Chip B sent 50s after the chip list, nothing in between")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A", "B").Chip(9, "A").Chip(58, "B").Done(59))
        .DeadFb("d1", "d2");

    /// <summary>A recommended FB row is slow; the recommended MP page behind it is playable first.</summary>
    public static ProbeScenario RecommendedMpBeforeSlowFb() => ProbeScenario.Named("Recommended MP playable at 11s, recommended FB ahead of it at 26s")
        .Fb("north", Play.HealthyAt(25))
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .DeadFb("d1")
        .Recommend("north")
        .Recommend("mp", RecommendationConfidence.Medium);

    /// <summary>The recommended row is slow; a later FB row is playable first.</summary>
    public static ProbeScenario FbBeforeSlowRecommended() => ProbeScenario.Named("FB playable at 6s, recommended row at 16s")
        .Fb("rec", Play.HealthyAt(15))
        .Fb("south", Play.HealthyAt(5))
        .DeadFb("d1")
        .Recommend("rec");

    /// <summary>Jon's normal MP (10s, then 1s per chip) with six chips, no healthy FB.</summary>
    public static ProbeScenario FastMp() => ProbeScenario.Named("Fast MP: six chips, 10s then 1s each")
        .Mp("mp", MpScript.Normal(["A", "B", "C", "D", "E", "F"]))
        .DeadFb("d1", "d2", "d3", "d4");

    /// <summary>MP chips that only ever carry a playlist (no rewritten segments).</summary>
    public static ProbeScenario PlaylistOnlyMp() => ProbeScenario.Named("MP chips playlist-only")
        .Mp("mp", MpScript.Normal(["A", "B", "C"], kind: ChipKind.PlaylistOnly))
        .Fb("fbA", Play.HealthyAt(5))
        .DeadFb("fbB", "fbC");

    /// <summary>MP lists five chips; A-C are misses, D and E are never sent; done at 15s (rule 6: one POST).</summary>
    public static ProbeScenario AllChipsMiss() => ProbeScenario.Named("MP lists 5 chips, every chip a miss, done at 15s")
        .Mp("mp", MpScript.Headers(9).Chips(9, "A", "B", "C", "D", "E")
            .Chip(10, "A", ChipKind.Miss)
            .Chip(11, "B", ChipKind.Miss)
            .Chip(12, "C", ChipKind.Miss)
            .Done(15))
        .DeadFb("d1", "d2", "d3");

    /// <summary>Two MP pages: page B waits for page A's harvest (LocalService runs one /mp).</summary>
    public static ProbeScenario TwoMpPages() => ProbeScenario.Named("Two MP pages, one /mp at a time")
        .Mp("mpA", MpScript.Normal(["A1", "A2", "A3", "A4", "A5"]))
        .Mp("mpB", MpScript.Normal(["B1", "B2", "B3"]))
        .DeadFb("d1", "d2", "d3", "d4", "d5", "d6")
        .WithoutPriorityWindow();

    /// <summary>The harvest fails midway: a 500 error line after 3 of 10 chips.</summary>
    public static ProbeScenario HarvestFaultsMidway() => ProbeScenario.Named("MP harvest 500s after 3 of 10 chips")
        .Mp("mp", MpScript.Headers(9).Chips(9, TenChips)
            .Chip(10, "Alpha")
            .Chip(11, "Bravo US")
            .Chip(12, "Charlie2")
            .Error(13))
        .DeadFb("d1", "d2", "d3", "d4")
        .WithoutPriorityWindow();

    /// <summary>LocalService fails /mp (500) before listing anything: the slot comes back on the error path.</summary>
    public static ProbeScenario MpErrorsBeforeListing() => ProbeScenario.Named("MP 500s at 5s before listing chips")
        .Mp("mp", MpScript.Headers(4).Error(5))
        .DeadFb("d1", "d2", "d3", "d4")
        .WithoutPriorityWindow();

    /// <summary>
    /// A match switch at t+20 while page A is still loading (chips would list at 25s):
    /// run 1 is cancelled, so its harvest is cancelled and run 2's page can POST at once.
    /// </summary>
    public static ProbeScenario CancelMidHarvest() => ProbeScenario.Named("Match switch at t+20 mid-harvest")
        .Mp("mpA", MpScript.Headers(8).Chips(25, "A1", "A2").Chip(26, "A1").Chip(27, "A2").Done(27.5))
        .DeadFb("d1", "d2")
        .ThenSwitchAt(20)
        .Mp("mpB", MpScript.Normal(["B1", "B2"]))
        .DeadFb("e1")
        .WithoutPriorityWindow();

    /// <summary>Every scenario, for the A1-A5 report (<see cref="ProbeSchedulingRulesTests.Acceptance"/>).</summary>
    public static readonly (string Name, Func<ProbeScenario> Scenario)[] All =
    [
        (nameof(SlowTenChipPage), SlowTenChipPage),
        (nameof(BuriedMp54Rows), BuriedMp54Rows),
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
        (nameof(AllChipsMiss), AllChipsMiss),
        (nameof(TwoMpPages), TwoMpPages),
        (nameof(HarvestFaultsMidway), HarvestFaultsMidway),
        (nameof(MpErrorsBeforeListing), MpErrorsBeforeListing),
        (nameof(ChipLateAfterListing), ChipLateAfterListing),
        (nameof(RecommendedMpBeforeSlowFb), RecommendedMpBeforeSlowFb),
        (nameof(FbBeforeSlowRecommended), FbBeforeSlowRecommended),
    ];

    // ---- the table ----

    public static readonly ProbeCase[] Current =
    [
        new("R1 54 rows: MP probed beside the recommended rows", "R1 R4",
            BuriedMp54Rows,
            StartOrderAmong(ProbeKind.Play, "f10", "f20", "f30", "f33"),
            StartsBy("mp40", 0.001),
            FirstPlayOn("f10"), FirstPlayBy(9.001),
            ChipsQueued("mp40", 3, by: 9.001)),

        new("R2 two dead FB probed at once (~20s each)", "R2",
            DeadFbsOnly,
            PlaysAtOnceReach(2), SlotsNeverExceed(2),
            RowEnds("d6", StreamResolutionStatus.Failed, 59.9, 60.1),
            NoIdleSlotWhileQueued()),

        new("R2 /mp holds a slot only until its chips are listed", "R2 R3 R4",
            MpPendingDeadFbs,
            SessionsAtMostUntilListed("mp"),
            MpHoldsNoSlotFrom(8.001),
            PlaysBesideHarvestAtLeast(2),
            LocalServiceSessionsNeverExceed(3),
            MpPostsAtOnceNeverExceed(1),
            SlotsNeverExceed(2),
            NoSlotHeldAtEnd()),

        new("R3/R6 normal MP: chips become their own rows; seed released at the chip list", "R2 R3 R5 R6 R10",
            NormalMp,
            SeedReturns("mp", 8.9, 9.1),
            ChipsQueued("mp", 5, by: 9.001),
            ChipsVisibleWithin("mp"),
            RowEnds("mp:E", StreamResolutionStatus.Healthy),
            PoolOrder("fbA", "mp", "mp:B", "mp:C", "mp:D", "mp:E"),
            SessionsAtMostUntilListed("mp"),
            MpPostCount("mp", 1),
            NoIdleSlotWhileQueued()),

        new("R9 slow MP never holds a healthy FB first play; a chip sent late still joins", "R7 R9 R10",
            SlowMpHealthyFb,
            FirstPlayOn("south"), FirstPlayBy(6.001),
            FirstPlayWithinOfFirstHealthy(),
            // Listed at 8s, nothing more until 60s: the chip row waits in the queue
            // (not the MP lane) and is probed when the chip arrives.
            RowEnds("mp", StreamResolutionStatus.Healthy, 60.9, 61.1),
            NoRowOfPageEnds("mp", StreamResolutionStatus.Deferred),
            PoolOrder("south", "mp")),

        new("R7 a chip sent 50s after the chip list is still probed (no stall Deferred)", "R7 R13",
            ChipLateAfterListing,
            RowEnds("mp", StreamResolutionStatus.Healthy, 9.9, 10.1),
            RowEnds("mp:B", StreamResolutionStatus.Healthy, 58.9, 59.1),
            NoRowOfPageEnds("mp", StreamResolutionStatus.Deferred),
            NoRowOfPageEnds("mp", StreamResolutionStatus.Failed),
            MpPostCount("mp", 1),
            NoIdleSlotWhileQueued(),
            NoSlotHeldAtEnd()),

        new("R9 first come first served: a recommended MP playable first plays before a slower recommended FB", "R9 R10",
            RecommendedMpBeforeSlowFb,
            FirstPlayOn("mp"), FirstPlayBy(11.001),
            FirstPlayWithinOfFirstHealthy(0.001),
            PoolOrder("mp", "mp:B", "north")),

        new("R9 first come first served: an FB playable first plays before a slower recommended row", "R9 R10",
            FbBeforeSlowRecommended,
            FirstPlayOn("south"), FirstPlayBy(6.001),
            FirstPlayWithinOfFirstHealthy(0.001),
            PoolOrder("south", "rec")),

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

        new("R9 slow ten-chip page: first play on a healthy FB while /mp harvests", "R9",
            SlowTenChipPage,
            FirstPlayOn("fb-north"), FirstPlayBy(13.001),
            FirstPlayWithinOfFirstHealthy()),

        new("R5 slow ten-chip page: /mp releases its place once the chip list arrives", "R2 R5",
            SlowTenChipPage,
            SeedReleasesSlotBy("mp-north", 8.401),
            SessionsAtMostUntilListed("mp-north"),
            MpHoldsNoSlotFrom(8.401),
            PlaysBesideHarvestAtLeast(2),
            LocalServiceSessionsNeverExceed(3)),

        new("R6 slow ten-chip page: all 10 chips queued as rows, none dropped", "R6 R7",
            SlowTenChipPage,
            ChipsQueued("mp-north", 10, by: 8.401),
            ChipsVisibleWithin("mp-north"),
            RowEnds("mp-north", StreamResolutionStatus.Healthy),
            RowEnds("mp-north:Juliet", StreamResolutionStatus.Healthy),
            MpPostCount("mp-north", 1)),

        new("R4 two FB at once beside a listed /mp harvest; the waiting chip never holds the MP lane", "R4 R7",
            MpPendingDeadFbs,
            PlaysBesideHarvestAtLeast(2),
            // d2 starts at 8s (the chip list frees /mp's slot), so six 20s dead FB end at 68s.
            RowEnds("d6", StreamResolutionStatus.Failed, 67.9, 68.1),
            RowEnds("mp", StreamResolutionStatus.Healthy, 100.9, 101.1),
            NoRowOfPageEnds("mp", StreamResolutionStatus.Deferred),
            NoIdleSlotWhileQueued()),

        new("R7 seed gives up after 30s with nothing listed; the retry queues the chips", "R2 R7 R13",
            SeedSilent,
            SeedReleasesSlotBy("mp", ProbeAcceptance.SeedGiveUpSeconds + 0.001),
            SessionsAtMostUntilListed("mp"),
            MpHoldsNoSlotFrom(ProbeAcceptance.SeedGiveUpSeconds + 0.001),
            ChipsQueued("mp", 2, by: 37.001),
            RowEnds("mp", StreamResolutionStatus.Healthy, 37.9, 38.1),
            RowEnds("mp:B", StreamResolutionStatus.Healthy, 38.9, 39.1)),

        new("R7 a playlist-only chip is handed over 30s after its playlist line; a chip never sent waits in the queue", "R5 R7",
            SeedNeverProbeReady,
            SeedReturns("mp", 7.9, 8.1),
            ChipsQueued("mp", 2, by: 8.001),
            RowEnds("mp", StreamResolutionStatus.Healthy, 39.9, 40.1),
            // B is never sent: its row waits in the queue (not the MP lane) and reads
            // the finished harvest's verdict when it ends at 300s.
            RowEnds("mp:B", StreamResolutionStatus.Failed, 299.9, 300.1),
            MpPostCount("mp", 1)),

        new("R12 the startup race never waits on a listed /mp (slow ten-chip page)", "R2 R12",
            () => SlowTenChipPage().WithStartupRaceDrain(),
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

        new("R6 every chip a miss: one POST /mp, every chip Failed from that harvest", "R3 R6",
            AllChipsMiss,
            MpPostCount("mp", 1),
            RowsEnd(StreamResolutionStatus.Failed, "mp", "mp:B", "mp:C", "mp:D", "mp:E"),
            RowEnds("mp:E", StreamResolutionStatus.Failed, 14.9, 15.1),
            NoIdleSlotWhileQueued()),

        new("R3 two MP pages: page B waits for page A's harvest; A's chips still probed", "R2 R3 R4",
            TwoMpPages,
            MpPostsAtOnceNeverExceed(1),
            // A's lane frees at 15s (A5's health check); B's POST then waits for a
            // LocalService slot (d1 ends at 20s): never 3 sessions while B loads.
            PostStartsBetween("mpB", 20, 20.001),
            MpPostCount("mpA", 1), MpPostCount("mpB", 1),
            RowsEnd(StreamResolutionStatus.Healthy, "mpA", "mpA:A2", "mpA:A3", "mpA:A4", "mpA:A5", "mpB", "mpB:B2", "mpB:B3"),
            RowEnds("mpA:A5", StreamResolutionStatus.Healthy, 14.9, 15.1),
            SessionsAtMostUntilListed("mpA"), SessionsAtMostUntilListed("mpB"),
            LocalServiceSessionsNeverExceed(3),
            PlaysBesideHarvestAtLeast(2),
            NoIdleSlotWhileQueued()),

        new("R13 harvest 500s midway: chips without a playlist are Deferred, never Failed", "R2 R13",
            HarvestFaultsMidway,
            RowsEnd(StreamResolutionStatus.Healthy, "mp", "mp:Bravo US", "mp:Charlie2"),
            RowsEnd(StreamResolutionStatus.Deferred, "mp:Delta IT", "mp:Echo FR", "mp:Foxtrot Arabic", "mp:Golf PT", "mp:Hotel TH", "mp:India VN", "mp:Juliet"),
            NoRowOfPageEnds("mp", StreamResolutionStatus.Failed),
            RowEnds("mp:Juliet", StreamResolutionStatus.Deferred, 13, 14.001),
            MpPostCount("mp", 1),
            MpHoldsNoSlotFrom(9.001),
            NoSlotHeldAtEnd()),

        new("R2 R13 /mp 500 before listing: slot back on the error path, row Deferred", "R2 R13",
            MpErrorsBeforeListing,
            NoRowOfPageEnds("mp", StreamResolutionStatus.Failed),
            RowEnds("mp", StreamResolutionStatus.Deferred),
            SessionsAtMostUntilListed("mp"),
            LocalServiceSessionsNeverExceed(2),
            NoSlotHeldAtEnd()),

        new("R3 match switch mid-harvest: the harvest is cancelled and the next page POSTs at once", "R3",
            CancelMidHarvest,
            PostStartsBetween("mpB", 20, 20.001),
            MpPostsAtOnceNeverExceed(1),
            NoRowOfPageEnds("mpA", StreamResolutionStatus.Failed),
            RowEnds("mpB:B2", StreamResolutionStatus.Healthy),
            LocalServiceSessionsNeverExceed(3),
            NoSlotHeldAtEnd()),
    ];

    /// <summary>Rules written down before the code does them. Empty: every rule is CURRENT.</summary>
    public static readonly ProbeCase[] Gaps = [];

    public static ProbeCase Find(string name) =>
        Current.Concat(Gaps).FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"No probe case named '{name}'");
}
