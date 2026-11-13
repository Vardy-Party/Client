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

    /// <summary>
    /// Rule 11: fbA plays at 6s with no backup, so finding carries on; fbB (a Referer soak
    /// link) joins at 8s and the window pauses finding while the prewarmer warms it.
    /// </summary>
    public static ProbeScenario PriorityWindow() => ProbeScenario.Named("First play, then the window holds only while the backup warms")
        .Fb("fbA", Play.SoakAt(5))
        .Fb("fbB", Play.SoakAt(7))
        .DeadFb("d1", "d2", "d3", "d4");

    // ---- top tier (rule 1: High-confidence recommendations get a 12s head start) ----

    /// <summary>Two High recommendations, both healthy inside the head start: they hold both slots; a fast FB and an MP page wait for one.</summary>
    public static ProbeScenario TwoHighHealthy() => ProbeScenario.Named("Two High recommendations healthy at 9s and 10s")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.HealthyAt(8))
        .Fb("h2", Play.HealthyAt(9))
        .DeadFb("d1")
        .Recommend("h1").Recommend("h2");

    /// <summary>The only High recommendation is slow (healthy at 31s): finding fills the other slot from 0s and plays first.</summary>
    public static ProbeScenario OneHighSlow() => ProbeScenario.Named("One High recommendation healthy at 31s")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.HealthyAt(30))
        .DeadFb("d1")
        .Recommend("h1");

    /// <summary>Both High recommendations are dead (20s each); a Low recommendation and the MP page are healthy.</summary>
    public static ProbeScenario HighsDead() => ProbeScenario.Named("Two High recommendations dead, Low and MP healthy")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("l1", Play.HealthyAt(5))
        .Fb("h1", Play.Dead())
        .Fb("h2", Play.Dead())
        .DeadFb("d1")
        .Recommend("h1").Recommend("h2")
        .Recommend("l1", RecommendationConfidence.Low);

    /// <summary>Four High recommendations (two dead, two healthy): the top tier keeps both slots while its FB rows wait.</summary>
    public static ProbeScenario FourHighs() => ProbeScenario.Named("Four High recommendations, two healthy")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.Dead())
        .Fb("h2", Play.HealthyAt(9))
        .Fb("h3", Play.HealthyAt(6))
        .Fb("h4", Play.Dead())
        .Recommend("h1").Recommend("h2").Recommend("h3").Recommend("h4");

    /// <summary>A High-recommended MP page: it takes the MP lane at 0s; the FB rows are finding and fill the other slot.</summary>
    public static ProbeScenario RecommendedMp() => ProbeScenario.Named("High-recommended MP page")
        .Mp("mp", MpScript.Normal(["A", "B", "C"]))
        .Fb("f1", Play.HealthyAt(3))
        .DeadFb("d1", "d2")
        .Recommend("mp");

    /// <summary>One High recommendation, single-use (cookie-bound), nothing else healthy: a fresh start /play, no backup.</summary>
    public static ProbeScenario SingleHealthyNoBackup() => ProbeScenario.Named("One healthy row (High, single-use), no backup")
        .Fb("h1", Play.SingleUseAt(5))
        .DeadFb("d1", "d2", "d3", "d4")
        .Recommend("h1");

    /// <summary>One High (healthy at 9s) and one Medium (healthy at 11s): the Medium row is the backup.</summary>
    public static ProbeScenario HighPlusMedium() => ProbeScenario.Named("One High healthy at 9s, one Medium at 11s")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.HealthyAt(8))
        .Fb("m1", Play.HealthyAt(10))
        .DeadFb("d1")
        .Recommend("h1")
        .Recommend("m1", RecommendationConfidence.Medium);

    /// <summary>No High, two Medium (one healthy, one dead): one Medium at a time, finding fills the other slot from 0s.</summary>
    public static ProbeScenario NoHighOnlyMedium() => ProbeScenario.Named("No High, two Medium")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("m1", Play.HealthyAt(6))
        .Fb("m2", Play.Dead())
        .DeadFb("d1")
        .Recommend("m1", RecommendationConfidence.Medium)
        .Recommend("m2", RecommendationConfidence.Medium);

    /// <summary>The High row is dead (20s); the Medium row beside it is healthy at 6s and plays.</summary>
    public static ProbeScenario HighDeadMediumHealthy() => ProbeScenario.Named("High dead, Medium healthy at 6s")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.Dead())
        .Fb("m1", Play.HealthyAt(5))
        .DeadFb("d1")
        .Recommend("h1")
        .Recommend("m1", RecommendationConfidence.Medium);

    /// <summary>
    /// Rule 11: the second High row is slow (healthy at 26s); a finding FB row turns
    /// playable first (13s) and is the backup only until the High row replaces it.
    /// </summary>
    public static ProbeScenario EarlyFbBeforeLateHigh() => ProbeScenario.Named("Early FB backup replaced by a later High row")
        .Fb("h1", Play.HealthyAt(8))
        .Fb("h2", Play.HealthyAt(25))
        .Fb("f1", Play.HealthyAt(3))
        .DeadFb("d1")
        .Recommend("h1").Recommend("h2");

    /// <summary>Rule 11: as <see cref="EarlyFbBeforeLateHigh"/>, with a slow Medium row in place of the second High.</summary>
    public static ProbeScenario EarlyFbBeforeLateMedium() => ProbeScenario.Named("Early FB backup replaced by a later Medium row")
        .Fb("h1", Play.HealthyAt(8))
        .Fb("m1", Play.HealthyAt(25))
        .Fb("f1", Play.HealthyAt(3))
        .DeadFb("d1", "d2")
        .Recommend("h1")
        .Recommend("m1", RecommendationConfidence.Medium);

    /// <summary>
    /// Rule 1: two High rows hold both slots; h1 fails fast (4s) and its slot goes to the
    /// Medium row at once, not to finding and not after the head start.
    /// </summary>
    public static ProbeScenario DeadHighSlotToMedium() => ProbeScenario.Named("A dead High row's slot goes to Medium at once")
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.Dead(4))
        .Fb("h2", Play.HealthyAt(20))
        .Fb("m1", Play.HealthyAt(5))
        .DeadFb("d1")
        .Recommend("h1").Recommend("h2")
        .Recommend("m1", RecommendationConfidence.Medium);

    /// <summary>
    /// Rule 1: the chips of a High-recommended MP page are High rows. Chip B is sent late
    /// (20s): it still replaces the earlier finding FB row as the backup (rule 11).
    /// </summary>
    public static ProbeScenario HighMpChips() => ProbeScenario.Named("High MP page: its chips are High")
        .Fb("h1", Play.HealthyAt(25))
        .Mp("mp", MpScript.Headers(8).Chips(8, "A", "B").Chip(9, "A").Chip(20, "B").Done(21))
        .Fb("f1", Play.HealthyAt(3))
        .DeadFb("d1")
        .Recommend("h1").Recommend("mp");

    /// <summary>
    /// Rule 1, "Medium limited to one slot always": three Medium rows, the first two dead
    /// (20s). When m1 and f1 both fail at 20s (after the 12s head start) only one Medium row
    /// takes a slot; finding takes the other.
    /// </summary>
    public static ProbeScenario MediumCapAfterHeadStart() => ProbeScenario.Named("Three Medium rows past the head start: one at a time")
        .Fb("m1", Play.Dead())
        .Fb("m2", Play.Dead())
        .Fb("m3", Play.HealthyAt(5))
        .Fb("f1", Play.Dead())
        .Fb("f2", Play.HealthyAt(6))
        .DeadFb("d1")
        .Recommend("m1", RecommendationConfidence.Medium)
        .Recommend("m2", RecommendationConfidence.Medium)
        .Recommend("m3", RecommendationConfidence.Medium);

    /// <summary>
    /// Rule 1, "Medium limited to one slot always", in a re-queue pass: m1, m2 and f1 are CDN
    /// unreachable at first and healthy when the orchestrator tries them again (2 min after
    /// run 1 ends at 33s). No head start then, but the session's tiers still hold m2 until m1
    /// yields at 158s; without the cap m2 would start at 155s, beside m1's /play.
    /// </summary>
    public static ProbeScenario MediumCapInReQueuePass() => ProbeScenario.Named("Two Medium rows re-queued after CDN unreachable: one at a time")
        .Fb("m1", Play.CdnUnreachableAt(2), retry: Play.HealthyAt(4))
        .Fb("m2", Play.CdnUnreachableAt(2), retry: Play.HealthyAt(4))
        .Fb("f1", Play.CdnUnreachableAt(2), retry: Play.HealthyAt(1))
        .Fb("f2", Play.HealthyAt(30))
        .Recommend("m1", RecommendationConfidence.Medium)
        .Recommend("m2", RecommendationConfidence.Medium)
        .ThenReResolveDeferredAfter(120);

    /// <summary><see cref="TwoHighHealthy"/> with no recommendations: no head start, behaviour unchanged.</summary>
    public static ProbeScenario NoRecs() => ProbeScenario.Named("No recommendations (TwoHighHealthy rows)")
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .Fb("f1", Play.HealthyAt(3))
        .Fb("h1", Play.HealthyAt(8))
        .Fb("h2", Play.HealthyAt(9))
        .DeadFb("d1");

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

    /// <summary>
    /// A High-recommended FB row is slow; with one High row, the Medium-recommended MP
    /// page takes the other slot from 0s (ahead of finding) and is playable first.
    /// </summary>
    public static ProbeScenario RecommendedMpBeforeSlowFb() => ProbeScenario.Named("Medium MP playable at 11s beside a slow High FB (26s)")
        .Fb("north", Play.HealthyAt(25))
        .Mp("mp", MpScript.Normal(["A", "B"]))
        .DeadFb("d1")
        .Recommend("north")
        .Recommend("mp", RecommendationConfidence.Medium);

    /// <summary>The High-recommended row is slow; a later FB row fills the other slot and is playable first.</summary>
    public static ProbeScenario FbBeforeSlowRecommended() => ProbeScenario.Named("FB playable at 6s beside a slow High row (26s)")
        .Fb("rec", Play.HealthyAt(25))
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
        (nameof(TwoHighHealthy), TwoHighHealthy),
        (nameof(OneHighSlow), OneHighSlow),
        (nameof(HighsDead), HighsDead),
        (nameof(FourHighs), FourHighs),
        (nameof(RecommendedMp), RecommendedMp),
        (nameof(SingleHealthyNoBackup), SingleHealthyNoBackup),
        (nameof(NoRecs), NoRecs),
        (nameof(HighPlusMedium), HighPlusMedium),
        (nameof(NoHighOnlyMedium), NoHighOnlyMedium),
        (nameof(HighDeadMediumHealthy), HighDeadMediumHealthy),
        (nameof(EarlyFbBeforeLateHigh), EarlyFbBeforeLateHigh),
        (nameof(EarlyFbBeforeLateMedium), EarlyFbBeforeLateMedium),
        (nameof(DeadHighSlotToMedium), DeadHighSlotToMedium),
        (nameof(HighMpChips), HighMpChips),
        (nameof(MediumCapAfterHeadStart), MediumCapAfterHeadStart),
        (nameof(MediumCapInReQueuePass), MediumCapInReQueuePass),
    ];

    // ---- the table ----

    public static readonly ProbeCase[] Current =
    [
        new("R1 54 rows: the two High rows take both slots, then the Medium row and MP fill them", "R1 R4",
            BuriedMp54Rows,
            TopTierIs("f10", "f20"), MediumTierIs("f30"),
            StartOrderAmong(ProbeKind.Play, "f10", "f20", "f30", "f33"),
            // Two High rows hold both slots until 9s; the slot f10 frees goes to the
            // Medium row, the one f20 frees to finding (the MP lane first).
            StartsNotBefore("f30", 9), StartsBy("f30", 9.001),
            NoFindingStartsBefore(9), PostStartsBetween("mp40", 9, 9.001),
            MediumRunningNeverExceeds(1),
            FirstPlayOn("f10"), FirstPlayBy(9.001),
            BackupIs("f20"), BackupReadyBy(10.001),
            ChipsQueued("mp40", 3, by: 21.001)),

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

        new("R1 R9 one High row: the Medium MP takes the other slot at 0s and plays first", "R1 R9 R10",
            RecommendedMpBeforeSlowFb,
            TopTierIs("north"), MediumTierIs("mp"),
            PostStartsBetween("mp", 0, 0.001),
            // d1 (finding) asks at once but waits: High and Medium hold both slots.
            StartsBy("d1", 0.001),
            FirstPlayOn("mp"), FirstPlayBy(11.001),
            FirstPlayWithinOfFirstHealthy(0.001),
            PoolOrder("mp", "mp:B", "north")),

        new("R1 R9 one slow High row: finding fills the other slot at 0s and plays first", "R1 R9 R10",
            FbBeforeSlowRecommended,
            StartsBy("south", 0.001),
            FirstPlayOn("south"), FirstPlayBy(6.001),
            FirstPlayWithinOfFirstHealthy(0.001),
            PoolOrder("south", "rec")),

        new("R11 the priority window holds only while the backup warms", "R11 R12",
            PriorityWindow,
            FirstPlayOn("fbA"), FirstPlayBy(6.001),
            FirstPlayOpens(FirstPlayOpenStrategy.StartKind.PlayProbedLink),
            StartsBy("d1", 6.001),
            BackupIs("fbB"), BackupReadyBy(15.001),
            PriorityHoldsUntilBackupReady(),
            NoBrowserProbeStartsWhilePriorityHolds(),
            StartsNotBefore("d2", 15), StartsBy("d2", 15.001)),

        new("R11 harvested MP chips are probed inside the priority window", "R11",
            NormalMp,
            FirstPlayOn("fbA"), FirstPlayBy(4.001),
            NoPriorityHold(),
            ChipProbedInPriorityWindow("mp:E"),
            RowEnds("mp:E", StreamResolutionStatus.Healthy, 14.9, 15.1)),

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
            NoPriorityHold(),
            NoIdleSlotWhileQueued()),

        new("R7 playlist-only chips are probed once the harvest ends", "R6 R7 R11",
            PlaylistOnlyMp,
            FirstPlayOn("fbA"), FirstPlayBy(6.001),
            SeedReturns("mp", 8.9, 9.1),
            RowEnds("mp", StreamResolutionStatus.Healthy, 13.4, 13.6),
            RowEnds("mp:C", StreamResolutionStatus.Healthy, 15.4, 15.6),
            PoolOrder("fbA", "mp", "mp:B", "mp:C"),
            NoPriorityHold()),

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

        // ---- top tier first (rule 1), first play (rule 12), backup (rule 11) ----

        new("R1 two High healthy: they hold both slots; first play on the first, the second is the backup", "R1 R11 R12",
            TwoHighHealthy,
            TopTierIs("h1", "h2"),
            // /mp asks at once but gets a slot only when h1's /play answers (8s).
            PostStartsBetween("mp", 8, 8.001), StartsNotBefore("f1", 9),
            FirstPlayOn("h1"), FirstPlayBy(9.001),
            FirstPlayOpens(FirstPlayOpenStrategy.StartKind.UseExisting),
            BackupIs("h2"), BackupReadyBy(10.001),
            NoPriorityHold(),
            PoolOrder("h1", "h2", "f1"),
            TopTierHeadStartHeld()),

        new("R1 one slow High: finding fills the other slot at 0s (MP lane first) and plays first", "R1 R9",
            OneHighSlow,
            TopTierIs("h1"),
            PostStartsBetween("mp", 0, 0.001), StartsBy("f1", 0.001),
            FirstPlayOn("mp"), FirstPlayBy(11.001),
            BackupReplaced("mp:B", "h1"),
            RowEnds("h1", StreamResolutionStatus.Healthy, 30.9, 31.1),
            TopTierHeadStartHeld()),

        new("R1 both High dead: they hold both slots until 20s; finding gets them then; first play on the Low recommendation", "R1 R9",
            HighsDead,
            TopTierIs("h1", "h2"),
            // Calls include the wait for a LocalService slot: both are held by the dead High rows until 20s.
            PostStartsBetween("mp", 20, 20.001), StartsNotBefore("l1", 20),
            FirstPlayOn("l1"), FirstPlayBy(26.001),
            TopTierHeadStartHeld()),

        new("R1 four High: top-tier FB rows keep the slots; MP waits while one is queued", "R1 R11",
            FourHighs,
            TopTierIs("h1", "h2", "h3", "h4"),
            FirstPlayOn("h2"), FirstPlayBy(10.001),
            StartsBy("h3", 10.001),
            // h4 (the last High FB row) starts at 17s; until then the MP lane waits.
            StartsNotBefore("mp", 17), StartsBy("mp", 17.001),
            NoFindingStartsBefore(17),
            BackupIs("h3"), BackupReadyBy(17.001),
            TopTierHeadStartHeld()),

        new("R1 R11 High-recommended MP page takes the MP lane at 0s; finding fills the gap and plays first; the page is the backup", "R1 R4 R11",
            RecommendedMp,
            TopTierIs("mp"),
            PostStartsBetween("mp", 0, 0.001), StartsBy("f1", 0.001),
            HighTierIncludes("mp", "mp:B", "mp:C"),
            StartsBy("mp:B", 11.001),
            FirstPlayOn("f1"), FirstPlayBy(4.001),
            // f1 plays; the backup is the first playable High row, not the next channel.
            BackupIs("mp"),
            TopTierHeadStartHeld()),

        new("R11 R12 single healthy row, single-use: fresh start /play, no backup, finding carries on", "R11 R12",
            SingleHealthyNoBackup,
            TopTierIs("h1"),
            FirstPlayOpens(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst),
            FirstPlayOn("h1"), FirstPlayBy(11.001),
            NoBackup(), NoPriorityHold(),
            StartsBy("d1", 11.001),
            FirstPlayWithinOfFirstHealthy()),

        new("R1 one High, one Medium: they hold the two slots; High plays, the Medium row is the backup", "R1 R11",
            HighPlusMedium,
            TopTierIs("h1"), MediumTierIs("m1"),
            StartsBy("m1", 0.001),
            FirstPlayOn("h1"), FirstPlayBy(9.001),
            BackupIs("m1"), BackupReadyBy(11.001),
            PostStartsBetween("mp", 8, 8.001), StartsNotBefore("f1", 9),
            PoolOrder("h1", "m1"),
            TopTierHeadStartHeld()),

        new("R1 no High, only Medium: one Medium slot, finding fills the other from 0s; never two Medium at once", "R1",
            NoHighOnlyMedium,
            TopTierIs(), MediumTierIs("m1", "m2"),
            StartsBy("m1", 0.001), PostStartsBetween("mp", 0, 0.001),
            StartsNotBefore("m2", 7),
            MediumRunningNeverExceeds(1),
            FirstPlayOn("m1"), FirstPlayBy(7.001),
            TopTierHeadStartHeld()),

        new("R1 High dead, Medium healthy: the Medium row plays; finding gets the slot the Medium row frees", "R1 R9",
            HighDeadMediumHealthy,
            TopTierIs("h1"), MediumTierIs("m1"),
            StartsBy("m1", 0.001),
            FirstPlayOn("m1"), FirstPlayBy(6.001),
            RowEnds("h1", StreamResolutionStatus.Failed, 19.9, 20.1),
            PostStartsBetween("mp", 5, 5.001),
            BackupIs("mp"),
            TopTierHeadStartHeld()),

        new("R1 Medium is limited to one slot after the head start too: m2 and f2 take the slots m1 and f1 free at 20s", "R1",
            MediumCapAfterHeadStart,
            TopTierIs(), MediumTierIs("m1", "m2", "m3"),
            StartsBy("m1", 0.001), StartsBy("f1", 0.001),
            StartsBy("m2", 20.001), StartsBy("f2", 20.001),
            StartsNotBefore("m3", 40),
            MediumRunningNeverExceeds(1),
            FirstPlayOn("f2")),

        new("R1 Medium is limited to one slot in a re-queue pass: m2 waits for m1, finding fills the other slot", "R1 R11 R13",
            MediumCapInReQueuePass,
            MediumTierIs("m1", "m2"),
            RowEnds("m1", StreamResolutionStatus.Deferred, 2, 2.001),
            FirstPlayOn("f2"),
            NthCallStartsBetween("m1", 2, 153, 153.001), NthCallStartsBetween("f1", 2, 153, 153.001),
            NthCallStartsBetween("m2", 2, 158, 158.001),
            MediumRunningNeverExceeds(1),
            PoolOrder("f2", "f1", "m1", "m2"),
            BackupReplaced("f1", "m1")),

        new("R11 an early FB row is the backup only until the second High row is playable", "R1 R11",
            EarlyFbBeforeLateHigh,
            FirstPlayOn("h1"), FirstPlayBy(9.001),
            BackupReplaced("f1", "h2"), BackupIs("h2"),
            PoolOrder("h1", "f1", "h2")),

        new("R11 an early FB row is the backup only until the Medium row is playable", "R1 R11",
            EarlyFbBeforeLateMedium,
            FirstPlayOn("h1"), FirstPlayBy(9.001),
            BackupReplaced("f1", "m1"), BackupIs("m1"),
            PoolOrder("h1", "f1", "m1")),

        new("R1 a dead High row's slot goes to the Medium row at once", "R1",
            DeadHighSlotToMedium,
            TopTierIs("h1", "h2"), MediumTierIs("m1"),
            RowEnds("h1", StreamResolutionStatus.Failed, 3.9, 4.1),
            StartsNotBefore("m1", 4), StartsBy("m1", 4.001),
            FirstPlayOn("m1"), FirstPlayBy(10.001),
            NoFindingStartsBefore(10),
            MediumRunningNeverExceeds(1)),

        new("R1 R11 the chips of a High MP page are High: probed as High, a late chip replaces a finding backup", "R1 R6 R11",
            HighMpChips,
            TopTierIs("h1", "mp"),
            HighTierIncludes("mp", "mp:B"),
            FirstPlayOn("mp"), FirstPlayBy(10.001),
            StartsBy("mp:B", 20.001),
            BackupReplaced("f1", "mp:B"), BackupIs("mp:B"),
            PoolOrder("mp", "f1", "mp:B")),

        new("R1 no recommendations: no head start, behaviour unchanged", "R1 R4 R9",
            NoRecs,
            NoHeadStart(),
            StartsBy("mp", 0.001),
            FirstPlayOn("f1"), FirstPlayBy(4.001),
            NoIdleSlotWhileQueued()),
    ];

    /// <summary>Rules written down before the code does them. Empty: every rule is CURRENT.</summary>
    public static readonly ProbeCase[] Gaps = [];

    public static ProbeCase Find(string name) =>
        Current.Concat(Gaps).FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"No probe case named '{name}'");
}
