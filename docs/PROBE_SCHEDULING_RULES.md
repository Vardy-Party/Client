# Probe scheduling rules

How the client should schedule stream probing (`GET /play` for FB rows, `POST /mp` for MP/V2 pages) against LocalService. Each rule is one sentence. **CURRENT** means the code does it today (file:line on `test/probe-schedule-timeline`, cut from `fix/mp-after-recommended-order` 024de12). **GAP** means it doesn't yet. Every rule is CURRENT as of 12 Nov 2026.

Facts the rules rely on (Jon, 12 Nov 2026):
- A normal `/mp` takes about 10s for the first chip, then about 1s per extra chip. Our 330s harvest cap is only a timeout.
- A dead FB link takes about 20s to fail.

Timeline tests: `tests/VardyParty.Streaming.Tests/Timeline/` (see "Timeline harness" below). The case names are in brackets.

## Rules

1. **Order.** Probe recommended rows first (high confidence before low), then every MP row, then the FB rows, each group in catalog order. With no usable recommendations, MP rows go first and the FB rows are rotated by the session salt. MP rows run in their own lane (rule 4), so the first MP row starts at once beside the first two FB rows.
   CURRENT: `StreamTestOrderPolicy.cs:50`, `:86-87`; `StreamRecommendationPolicy.cs:93-119` (#162); lanes at `StreamResolver.cs:122-141`. [R1 Italy v France]
2. **Concurrency.** LocalService runs at most 2 `/play` probes at once, before first play and after it, plus one `/mp` (rules 3-4).
   CURRENT: `ResolverRaceThrottle.cs:12`; `/play` takes a slot at `LocalLanPlayService.cs:437`; FB window of 2 at `StreamResolver.cs:128` (batch size 2 from `StreamResolutionOrchestrator.cs:396-398`). Nothing in history drops this to 1 after first play: after first play there is a pause (rule 11), then 2 again. [R2 two dead FB]
3. **MP single-flight.** At most one MP row is probed at a time, and LocalService runs at most one `POST /mp` at a time.
   CURRENT: MP lane of one at `StreamResolver.cs:122`, `:151-164`; `_mpPostGate` held from the POST until that harvest ends (`LocalLanPlayService.cs:116`, `:790`, released at `:1019-1022`). [R4]
4. **MP has its own lane.** An in-flight `/mp` must not take an FB place: two FB probes run beside one `/mp`.
   CURRENT: `/mp` skips the `/play` throttle (`LocalLanPlayService.cs:425-428`) and the FB window counts FB rows only (`StreamResolver.cs:128`). Before: `/mp` took a window place and a throttle slot, so FB rows 32 to 36 ran one at a time in the Czechia log (20:12:25 to 20:14:24 UK time). [R4 two FB at once while /mp is pending]
5. **Slot release.** `/mp` gives back its MP place as soon as the chip list or the first chip arrives, and the harvest carries on in the background.
   CURRENT: the seed answers with the chip list (`MpHarvestSession.cs:114`, `:145`, waits at `:208`); the resolver re-queues the seed row as its first chip (`StreamResolver.cs:232-262`, `:386-398`). Before: the place was held until a probe-ready chip or the end of the harvest, 111s for Czechia. [R5 Czechia; R3/R6 normal MP]
6. **Chips are rows.** Every chip LocalService lists becomes its own queued row, even when the seed has no probe-ready playlist. The catalog MP row becomes the first chip (it keeps its catalog place); the other chips are new rows, appended to the pool as they turn healthy.
   CURRENT: `ChipListSeed` (`StreamResolver.cs:594`), re-queue at `:232-262`; chips listed later on a yield at `:267-283`; a finished harvest with chips hands over the list, not null (`MpHarvestSession.cs:367`). Before: a seed with no probe-ready chip returned null and Czechia's 10 chips were dropped. [R6 Czechia; R9 all FB dead]
7. **Seed deadline.** Nothing waits more than 30s (`MpHarvestSession.ProbeReadyWait`, configurable) for probe-ready segments: the seed gives up 30s after `POST /mp`, and a chip that only has a playlist is handed over as is 30s after its playlist line (or when the harvest ends). A seed that gives up with nothing listed is infrastructure, not a miss: the row is retried (2s, 5s), then Deferred and re-queued, and the next ask picks up the chip list.
   CURRENT: `MpHarvestSession.cs:18`, seed deadline at `:239`, chip deadline at `:258`; harvest timeout on the same clock (`LocalLanPlayService.cs:840`, still 330s at `:770`). [R7 seed gives up; R7 playlist-only chip; R7 playlist-only chips]
8. **Dead FB is a verdict.** A dead FB link fails in about 20s, frees its slot, and is not retried by the client.
   CURRENT: a null answer is the stream's verdict (`LocalLanPlayService.cs:417-421`) and the row is Failed at `StreamResolver.cs:400-410`. [R2 two dead FB]
9. **First play never waits on `/mp`.** A healthy row is held only by earlier non-MP rows that are still probing. An MP row still in `/mp` never holds it. An MP chip holds at its page's catalog place, so when every FB row is dead a healthy chip plays.
   CURRENT: `FirstPlayHoldPolicy.cs:39-70`, used at `StreamResolutionOrchestrator.cs:262-279` (#162). [R9 slow MP; R9 Czechia; R9 all FB dead; R9 chip A dead]
10. **Stable channel numbers.** A row takes the next channel number when it joins the pool and keeps it. MP rows (the catalog MP row, now its first chip, and its siblings) are appended as they turn healthy after first play.
    CURRENT: `StreamSwitchingService.cs:9-16`, `:89`; `FirstPlayHoldPolicy.IsReleasable` (`FirstPlayHoldPolicy.cs:39`). [R3/R6 normal MP, pool order; fast MP]
11. **Priority window.** After first play, no new browser probe (`/play` or a new `/mp`) starts for up to 30s (or until prewarm depth is warm); probes already in flight finish. MP chips the running harvest already holds need nothing from LocalService and are probed during the window.
    CURRENT: `StreamResolutionOrchestrator.cs:247`, `:1610-1652`; `FirstPlayOpenStrategy.cs:21`; `StreamResolver.cs:118-126` (only ready chips during a race), woken by the race end or a harvest change at `:166-215`; readiness from `IMpHarvestProbe` (`LocalLanPlayService.cs:444-466`, `MpHarvestSession.cs:91`). [R11; R11 harvested MP chips]
12. **Prewarm and race slots.** The prewarmer and the startup race may take slots during the hold, and a startup race never waits on an in-flight `/mp`.
    CURRENT: `UpcomingStreamPrewarmer.cs:110`, `ResolverRaceThrottle.cs:108`; `/mp` holds no throttle slot (`LocalLanPlayService.cs:425-428`), so `DrainOthersAsync` (`StreamResolutionOrchestrator.cs:1382-1384`, `ResolverRaceThrottle.cs:80`) waits only for `/play`. Before: Czechia's drain would have waited until t+111s. [R12]
13. **Infrastructure is Deferred.** A CDN-unreachable answer (504, connect failure, manifest 403/5xx) or a LocalService outage marks the row Deferred, not Failed. The row is not reported and never holds first play.
    CURRENT: `StreamResolver.cs:363-384`, and the health verdict after it. LocalService outages (and a seed that gave up with nothing listed, rule 7) are retried after 2s and 5s (`StreamResolver.cs:20`, `:676`) and re-queued after 5s and 15s (`StreamResolutionOrchestrator.cs:37`). [R13; R7 seed gives up]
14. **Retry before switch.** A stalled stream is retried in place up to 3 times (reload the playlist, then a fresh resolve) before playback moves to the next channel. A confirmed-dead error switches at once.
    CURRENT: `PlaybackPolicy.SameStreamRecovery.cs:58`, `:191`, `:197-200`. Tested by `SameStreamRecoveryBusinessRulesTests`; this is outside the probe timeline.

## Acceptance (numbers PROPOSED, for Jon to set)

These are measured on the timeline by `Expect.*` in the harness. The constants are in `ProbeAcceptance`.

- **A1.** First play comes within **2s** of the first healthy verdict, unless an earlier non-MP row is still probing (rule 9). `FirstPlayWithinOfFirstHealthy`
- **A2.** MP chips are queued as rows within **2s** of LocalService listing them. `ChipsVisibleWithin`
- **A3.** No slot sits idle while a candidate is queued, outside the priority window: **1s** total tolerance. `NoIdleSlotWhileQueued`
- **A4.** A seed with no probe-ready chip gives up within **30s** of `POST /mp`. `SeedReleasesSlotBy`
- **A5.** A startup race finishes draining within **21s** of first play (one dead FB plus 1s). `DrainWithin`

Settled (Jon, 12 Nov 2026): chips that are already harvested may be probed during the priority window (rule 11).

Results per scenario are printed by `ProbeSchedulingRulesTests.Acceptance` (run with `--logger "console;verbosity=detailed"`). One known miss: `SeedSilent` A2. LocalService lists the chips at t+34s, after the seed gave up at t+30s, and the resolver's retry asks again at t+37s (3s, not 2s).

## Timeline harness

`ProbeTimelineHarness` runs the real scheduling code:
- `StreamTestOrderPolicy`
- `StreamResolver`
- `LocalLanPlayService`, including the `/mp` NDJSON harvest and `MpHarvestSession`
- `ResolverRaceThrottle`
- `FirstPlayHoldPolicy`
- `StreamSwitchingService`

Only LocalService (HTTP) and the CDN health check are scripted. They run on `MilestoneClock`, a `TimeProvider` that jumps straight to the next due timer, so a 111s harvest runs in milliseconds.

A case is one row in `ProbeSchedulingScenarios` (catalog rows, LocalService milestones, expected outcomes). Example:

```csharp
new("R9 slow MP never holds a healthy FB first play", "R9",
    () => ProbeScenario.Named("...")
        .Mp("mp", MpScript.Headers(8).Chips(8, "A").Chip(60, "A").Done(61))
        .Fb("south", Play.HealthyAt(5))
        .DeadFb("d1", "d2"),
    FirstPlayOn("south"), FirstPlayBy(6.001), PoolOrder("south", "mp")),
```

How the tests are organised:
- CURRENT cases run as a theory.
- `Acceptance` runs A1-A5 on every scenario in `ProbeSchedulingScenarios.All`.
- A new rule the code doesn't do yet goes in `Gaps` with a skipped `Gap_*` fact (`NoOpenGaps` fails while any is listed, as a reminder). There are none now.

The harness records the resolver's two lanes (FB window, MP lane) from its "Starting resolve" logs and yields; A3 counts time when a lane has a free place and a queued row for it.

What the harness does not model:
- the orchestrator's re-queue passes for Deferred rows;
- the prewarmer, and its early release of the priority window;
- the `/play` timeout (65s), which is not on a `TimeProvider`. The `/mp` harvest timeout (330s) and the 30s seed and chip deadlines are on `LocalLanPlayService.Time`.

Known limits of the MP rules:
- Labels LocalService reveals only after the seed handed over its list are added when a chip row yields; a label that first appears after the last chip row yielded is not added.
- A chip LocalService lists but never sends waits until the harvest ends (300s in `SeedNeverProbeReady`), holding the MP lane for that page.
- A different MP page waits for the running harvest to end (`_mpPostGate`), as LocalService did before: it is single-threaded for `/mp`.
