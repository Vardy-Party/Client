# Probe scheduling rules

How the client should schedule stream probing (`GET /play` for FB rows, `POST /mp` for MP/V2 pages) against LocalService. Each rule is one sentence. **CURRENT** means the code does it today; the reference after it names the type and method that does it (method names, not line numbers, so they stay true as the files change). **GAP** means it doesn't yet. Every rule is CURRENT as of 13 Nov 2026.

Facts the rules rely on (Jon, 12 Nov 2026):
- A normal `/mp` takes about 10s for the first chip, then about 1s per extra chip. Our 330s harvest cap is only a timeout.
- A dead FB link takes about 20s to fail.

Decided (Jon, 13 Nov 2026): "/mp holds a slot only until the channel list arrives." A new harvest counts as one of LocalService's 2 sessions while the page loads, then gives the slot back and carries on in the background on the single `/mp` gate (rules 2, 4, 5).

Decided (Jon, 13 Nov 2026): "first come first served on playable streams." No row holds first play: the first row to get a healthy, playable verdict plays first (rule 9).

Decided (Jon, 13 Nov 2026): "Keep the recommended head start in the startup race." The one exception to rule 9 is the startup race: the recommended leg gets up to 1.5s of grace over an already-playable partner (rule 12).

Timeline tests: `tests/VardyParty.Streaming.Tests/Timeline/` (see "Timeline harness" below). The case names are in brackets.

## Rules

1. **Order.** Probe recommended rows first (high confidence before low), then every MP row, then the FB rows, each group in catalog order. With no usable recommendations, MP rows go first and the FB rows are rotated by the session salt. MP rows run in their own lane (rule 4), so the first MP row starts at once beside the first FB rows.
   CURRENT: `StreamTestOrderPolicy.Build`; `StreamRecommendationPolicy.SpreadDiscoveryOrder` and `IsMpLike` (#162); lanes in `StreamResolver.ResolveStreamsIncrementallyAsync` (`FillWindow`, `NextMp`). [R1 54 rows]
2. **Concurrency.** LocalService runs at most 2 sessions at once, before first play and after it. A session is a `/play` probe, or a new `/mp` harvest until LocalService lists the page's chips (or the seed gives up, or the POST fails or is cancelled). After that the harvest holds no slot.
   CURRENT: `ResolverRaceThrottle.MaxConcurrent` and `EnterAsync`; `/play` takes a slot in `LocalLanPlayService.ResolveM3U8UrlCoreAsync`; `/mp` takes one in `LocalLanPlayService.CallMpEndpointCoreAsync` after the `/mp` gate and gives it back in `ReleaseSlotWhenListedAsync` (chip list, first chip, harvest end, or `MpHarvestSession.ProbeReadyWait`) or in the `finally` of `ReadMpHarvestAsync` (error, cancel); FB window of 2 in `StreamResolver.FillWindow` (batch size 2 from `StreamResolutionOrchestrator.ResolveAsync`). Nothing drops this to 1 after first play: after first play there is a pause (rule 11), then 2 again. [R2 two dead FB; R2 /mp holds a slot only until its chips are listed; R5 slow ten-chip page; R2 R13 /mp 500 before listing]
3. **MP single-flight.** At most one MP row is probed at a time, and LocalService runs at most one `POST /mp` at a time. A row for another MP page waits in the queue while a harvest runs (it does not sit in the MP lane), and a harvest nobody wants any more is cancelled so the next page can POST.
   CURRENT: MP lane of one in `StreamResolver.FillWindow`/`NextMp`, which asks `IMpHarvestProbe.CanStartMp`; `_mpPostGate` held from the POST until that harvest ends (`LocalLanPlayService.CallMpEndpointCoreAsync`, released in the `finally` of `ReadMpHarvestAsync`); the harvest runs on its own token and is cancelled by `ReleaseMpWant` only when every caller and resolver run that wanted the page (`WantMpPage`) was cancelled. [R3 two MP pages; R3 match switch mid-harvest]
4. **Two FB beside the harvest.** Once the page's chips are listed, two FB probes run beside the background harvest; while the page loads, `/mp` and one FB share the 2 sessions.
   CURRENT: the FB window counts FB rows only (`StreamResolver.FillWindow`), and the throttle slot rule is rule 2. Before #162: `/mp` held a window place and a throttle slot for the whole harvest, so FB rows ran one at a time for 111s in the Czechia v Spain log (12 Nov 2026, 20:12:25 to 20:14:24 UK time). [R4 two FB at once beside a listed /mp harvest; R2 /mp holds a slot only until its chips are listed]
5. **Slot release.** `/mp` gives back its MP place and its throttle slot as soon as the chip list or the first chip arrives; the harvest carries on in the background on the `/mp` gate alone.
   CURRENT: the seed answers with the chip list (`MpHarvestSession.WaitForAsync`, `SeedFallback`, `ChipList`); the resolver re-queues the seed row as its first chip (`StreamResolver.ChipListSeed`, re-queue in `ResolveStreamsIncrementallyAsync`); the slot comes back in `LocalLanPlayService.ReleaseSlotWhenListedAsync` (`MpHarvestSession.Listed`). Before: the place was held until a probe-ready chip or the end of the harvest, 111s for Czechia v Spain. [R5 slow ten-chip page; R3/R6 normal MP]
6. **Chips are rows.** Every chip LocalService lists becomes its own queued row, even when the seed has no probe-ready playlist. The catalog MP row becomes the first chip (it keeps its catalog place); the other chips are new rows, appended to the pool as they turn healthy. A finished harvest answers its chips from what it saw, so one page is POSTed once.
   CURRENT: `StreamResolver.ChipListSeed` and the re-queue in `ResolveStreamsIncrementallyAsync`; chips listed later on a yield (`EnqueueMpSiblingsNext`); a finished harvest with chips hands over the list, not null (`MpHarvestSession.SeedFallback`); a finished, fault-free harvest is reused for its page for `MpHarvestSession.VerdictMemory` (60s) so each chip reads its verdict instead of re-POSTing (`MpHarvestSession.CanReuseFor`). Before: a seed with no probe-ready chip returned null and Czechia v Spain's 10 chips were dropped; a page whose chips all missed was POSTed again per chip. [R6 slow ten-chip page; R6 every chip a miss; R9 all FB dead]
7. **Deadlines.** Nothing waits in a slot or the MP lane for LocalService. The seed gives up 30s (`MpHarvestSession.ProbeReadyWait`) after `POST /mp` with nothing listed; that is retried (2s, 5s), then Deferred and re-queued, never Failed. A chip that only has a playlist is handed over as is 30s after its playlist line (or when the harvest ends). A chip that is listed but not sent waits in the queue, outside the MP lane, until LocalService sends it or the harvest ends, however long the harvest is silent, so a chip sent late is still probed; a direct caller that waits for it (not the resolver) fails as infrastructure after 30s with no new chip line.
   CURRENT: `MpHarvestSession.DefaultProbeReadyWait` (per session, set in the constructor); seed deadline in `MpHarvestSession.WaitForAsync` (`SeedTimedOut`); playlist-only deadline in `PlaylistWaitOver`, woken by a timer (`ScheduleWake`); `StreamResolver.NextMp` only starts a chip the harvest can answer (`IMpHarvestProbe.CanStartMp`, `MpHarvestSession.IsChipReady`) and otherwise waits for `WaitForMpHarvestChangeAsync`, so a waiting chip never holds the MP lane; direct-wait stall deadline in `MpHarvestSession.WaitForAsync` (`ChipTimedOut`, restarted by `Progress`); harvest timeout on the same clock (`LocalLanPlayService.MpHarvestTimeout`, 330s). [R7 seed gives up; R7 a playlist-only chip; R7 playlist-only chips; R7 a chip sent 50s after the chip list; R9 slow MP; R4 two FB at once beside a listed /mp harvest]
8. **Dead FB is a verdict.** A dead FB link fails in about 20s, frees its slot, and is not retried by the client.
   CURRENT: a null answer is the stream's verdict (`LocalLanPlayService.CallPlayEndpointAsync`) and the row is Failed in `StreamResolver.ResolveAndTestStreamInternalAsync`. [R2 two dead FB]
9. **First come, first served.** The first row to get a healthy, playable verdict plays first, recommended or not, MP or FB. No row holds it behind an earlier row that is still probing; try-order (rule 1) only decides which rows are probed first. The one exception is the startup race (rule 12).
   CURRENT: every healthy row joins the pool the moment its verdict lands (`StreamResolutionOrchestrator.ResolveAsync`, `JoinPool`). Before #162: a healthy row was held until every earlier non-MP row had yielded (`FirstPlayHoldPolicy`, now removed). [R9 first come first served (both cases); R9 slow MP; R9 slow ten-chip page; R9 all FB dead; R9 chip A dead]
10. **Stable channel numbers.** A row takes the next channel number when it joins the pool and keeps it (append only), so channel numbers follow verdict order.
    CURRENT: `StreamSwitchingService.AddHealthyStream`, called from `StreamResolutionOrchestrator.ResolveAsync` (`JoinPool`). [R3/R6 normal MP, pool order; fast MP; R9 first come first served]
11. **Priority window.** After first play, no new browser probe (`/play` or a new `/mp`) starts for up to 30s (or until prewarm depth is warm); probes already in flight finish. MP chips the running harvest already holds need nothing from LocalService and are probed during the window.
    CURRENT: `StreamResolutionOrchestrator.BeginPlaybackPriorityWindow`, `ReleasePlaybackPriorityWhenReadyAsync`, `EndPlaybackPriorityWindow`; `FirstPlayOpenStrategy.PlaybackPrioritySatisfied`; only ready chips during a race (`StreamResolver.NextMp(readyOnly)`), woken by the race end or a harvest change (`WakeAsync`, which waits only on what the loop is blocked by); readiness from `IMpHarvestProbe.IsMpChipReady` (`MpHarvestSession.IsChipReady`). [R11; R11 harvested MP chips]
12. **Prewarm and race slots.** The prewarmer and the startup race may take slots during the hold, and a startup race never waits on a `/mp` whose chips are listed. In the startup race the recommended leg keeps a head start: when the partner is already playable (video) and the recommended leg is still resolving, the racer waits up to 1.5s for the recommended leg before playing the partner (Jon, 13 Nov 2026; the one exception to rule 9).
    CURRENT: `UpcomingStreamPrewarmer.TickAsync`, `ResolverRaceThrottle.MarkRaceLeg`; `/mp` holds a throttle slot only until its chips are listed (rule 2), so `ResolverRaceThrottle.DrainOthersAsync` (called from `StreamResolutionOrchestrator.RaceStartAsync`) waits at most for a page that is still loading (30s at most, rule 7), then only for `/play`. Before #162: Czechia v Spain's drain would have waited until t+111s. [R12] Head start: `PlaybackRacePolicy.RecommendedGrace` (1.5s), applied by `PlaybackRacePolicy.Decide` in `PlaybackUrlRacer.RaceAsync` (`PlaybackRaceBusinessRulesTests`). Note: `RaceAsync` has no production caller today; the live first-picture path (`StreamResolutionOrchestrator.RaceStartAsync`, `FirstPlayOpenStrategy`) opens the start row alone and only warms the partner for Next, so it never plays the partner ahead of the start row and has no grace timer.
13. **Infrastructure is Deferred.** A CDN-unreachable answer (504, connect failure, manifest 403/5xx), a LocalService outage, an `/mp` harvest that fails or stalls, or a cancelled shared harvest marks the row Deferred, not Failed. The row is not reported and never holds first play.
    CURRENT: `StreamResolver.ResolveAndTestStreamInternalAsync` and the health verdict after it. LocalService outages (and the `/mp` deadlines, rule 7) are retried after 2s and 5s (`StreamResolver.InfrastructureRetryDelays`, `ResolveWithInfrastructureRetryAsync`) and re-queued after 5s and 15s (`StreamResolutionOrchestrator.DeferredRequeueDelays`). A harvest that already failed answers its chips with `LocalServiceUnavailableException.FromFinishedHarvest` (not retried in place, straight to Deferred). An `OperationCanceledException` that is not the caller's own cancellation becomes a retryable `LocalServiceUnavailableException` (`StreamResolver.ResolveM3U8ResponseInternalAsync`). [R13; R7 seed gives up; R13 harvest 500s midway; R2 R13 /mp 500 before listing]
14. **Retry before switch.** A stalled stream is retried in place up to 3 times (reload the playlist, then a fresh resolve) before playback moves to the next channel. A confirmed-dead error switches at once.
    CURRENT: `PlaybackPolicy.ShouldRetrySameStream`, `NextSameStreamRetryStep`, `ClassifyPlaybackError`. Tested by `SameStreamRecoveryBusinessRulesTests`; this is outside the probe timeline.

## Acceptance (numbers PROPOSED, for Jon to set)

These are measured on the timeline by `Expect.*` in the harness. The constants are in `ProbeAcceptance`.

- **A1.** First play comes within **2s** of the first healthy verdict (rule 9). The startup race's recommended head start (up to 1.5s, rule 12) is the one exception; the timeline harness does not model the racer. `FirstPlayWithinOfFirstHealthy`
- **A2.** MP chips are queued as rows within **2s** of LocalService listing them. `ChipsVisibleWithin`
- **A3.** No slot sits idle while a candidate is queued, outside the priority window: **1s** total tolerance. `NoIdleSlotWhileQueued`
- **A4.** A seed with no probe-ready chip gives up within **30s** of `POST /mp`. `SeedReleasesSlotBy`
- **A5.** A startup race finishes draining within **21s** of first play (one dead FB plus 1s). `DrainWithin`

Also checked per case (rules 2-3): `LocalServiceSessionsNeverExceed`, `SessionsAtMostUntilListed` (at most 2 sessions until the chip list), `MpHoldsNoSlotFrom`, `NoSlotHeldAtEnd`, `PlaysBesideHarvestAtLeast` (2 FB beside the background harvest), `MpPostsAtOnceNeverExceed(1)`, `MpPostCount`.

Settled (Jon, 12 Nov 2026): chips that are already harvested may be probed during the priority window (rule 11).

Results per scenario are printed by `ProbeSchedulingRulesTests.Acceptance` (run with `--logger "console;verbosity=detailed"`). One known miss: `SeedSilent` A2. LocalService lists the chips at t+34s, after the seed gave up at t+30s, and the resolver's retry asks again at t+37s (3s, not 2s).

## Timeline harness

`ProbeTimelineHarness` runs the real scheduling code:
- `StreamTestOrderPolicy`
- `StreamResolver`
- `LocalLanPlayService`, including the `/mp` NDJSON harvest and `MpHarvestSession`
- `ResolverRaceThrottle`
- `StreamSwitchingService` (rows join the pool on their verdict, as in `StreamResolutionOrchestrator`)

Only LocalService (HTTP) and the CDN health check are scripted. They run on `MilestoneClock`, a `TimeProvider` that jumps straight to the next due timer, so a 111s harvest runs in milliseconds. Between milestones the harness waits for the thread pool to go quiet (`SpinWait`, not a hot spin); if it does not within 5s the run fails with the timeline so far instead of advancing the clock early.

A case is one row in `ProbeSchedulingScenarios` (catalog rows, LocalService milestones, expected outcomes). Example:

```csharp
new("R9 slow MP never holds a healthy FB first play", "R9",
    () => ProbeScenario.Named("...")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A").Chip(20, "A").Done(21))
        .Fb("south", Play.HealthyAt(5))
        .DeadFb("d1", "d2"),
    FirstPlayOn("south"), FirstPlayBy(6.001), PoolOrder("south", "mp")),
```

Scenario provenance: `SlowTenChipPage` is the Czechia v Spain Windows run (12 Nov 2026, 20:12:25 UK time) and `BuriedMp54Rows` is Italy v France (54 rows, MP page at catalog row 41); the tests use made-up names.

How the tests are organised:
- CURRENT cases run as a theory.
- `Acceptance` runs A1-A5 on every scenario in `ProbeSchedulingScenarios.All`.
- A new rule the code doesn't do yet goes in `Gaps` with a skipped `Gap_*` fact (`NoOpenGaps` fails while any is listed, as a reminder). There are none now.

The harness records the resolver's two lanes (FB window, MP lane) from its "Starting resolve" logs and yields; A3 counts time when a lane has a free place and a queued row for it. A scenario can switch match mid-run (`ThenSwitchAt`): run 1 is cancelled and run 2 starts on the same `LocalLanPlayService`; the scripted LocalService stops a `/mp` when the client closes the connection.

What the harness does not model:
- the orchestrator's re-queue passes for Deferred rows;
- the prewarmer, and its early release of the priority window;
- the `/play` timeout (65s), which is not on a `TimeProvider`. The `/mp` harvest timeout (330s) and the 30s deadlines are on `LocalLanPlayService.Time`.

Known limits of the MP rules:
- Labels LocalService reveals only after the seed handed over its list are added when a chip row yields; a label that first appears after the last chip row yielded is not added.
- A different MP page waits for the running harvest to end (`_mpPostGate`): LocalService is single-threaded for `/mp`. Its rows wait in the queue, not in the MP lane.
- A listed chip LocalService never sends keeps the resolver run open (queued, holding no slot or lane) until the harvest ends, at most `LocalLanPlayService.MpHarvestTimeout` (330s).
