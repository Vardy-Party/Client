# Probe scheduling rules

How the client should schedule stream probing (`GET /play` for FB rows, `POST /mp` for MP/V2 pages) against LocalService. Each rule is one sentence. **CURRENT** means the code does it today (file:line on `test/probe-schedule-timeline`, cut from `fix/mp-after-recommended-order` 024de12). **GAP** means it doesn't yet.

Facts the rules rely on (Jon, 12 Nov 2026):
- A normal `/mp` takes about 10s for the first chip, then about 1s per extra chip. Our 330s harvest cap is only a timeout.
- A dead FB link takes about 20s to fail.

Timeline tests: `tests/VardyParty.Streaming.Tests/Timeline/` (see "Timeline harness" below). The case names are in brackets.

## Rules

1. **Order.** Probe recommended rows first (high confidence before low), then every MP row, then the FB rows, each group in catalog order. With no usable recommendations, MP rows go first and the FB rows are rotated by the session salt.
   CURRENT: `StreamTestOrderPolicy.cs:50`, `:86-87`; `StreamRecommendationPolicy.cs:93-119` (#162). [R1 Italy v France]
2. **Concurrency.** LocalService runs at most 2 browser probes at once, with `/play` and `/mp` sharing the cap, before first play and after it.
   CURRENT: `ResolverRaceThrottle.cs:12`; `LocalLanPlayService.cs:420`; window of 2 at `StreamResolutionOrchestrator.cs:396-398`. Nothing in history drops this to 1 after first play: after first play there is a pause (rule 11), then 2 again. [R2 two dead FB]
3. **MP single-flight.** At most one `/mp` is in flight at a time. While it runs, the window skips forward to FB rows.
   CURRENT: `StreamResolver.cs:109`.
4. **MP has its own lane.** An in-flight `/mp` must not take an FB place: two FB probes run beside one `/mp`.
   GAP: `/mp` takes one of the 2 window places and one throttle slot (`StreamResolver.cs:101-131`, `LocalLanPlayService.cs:420`), so only one FB runs beside it. The Czechia log shows it: FB rows 32 to 36 ran one at a time from 20:12:25 to 20:14:24 UK time. [TODAY one FB at a time; GAP R4]
5. **Slot release.** `/mp` gives back its slot and its MP place as soon as the chip list or the first chip arrives, and the harvest carries on in the background.
   GAP: the slot is held until the seed resolves (`LocalLanPlayService.cs:420`, `:767-768`). With no probe-ready chip, that is the end of the harvest: 111s for Czechia. [TODAY Czechia; GAP R5]
6. **Chips are rows.** Every chip LocalService lists becomes its own queued row, even when the seed has no probe-ready playlist.
   - CURRENT when the seed succeeds: `StreamResolver.cs:164-165`. [R3/R6 normal MP]
   - GAP when it doesn't: a null seed returns no siblings (`StreamResolver.cs:283-292`, then `SiblingsFrom` at `:474-476`), so Czechia's 10 chips were dropped. [TODAY Czechia; GAP R6]
7. **Seed deadline.** A seed with no probe-ready chip gives up after 30s (proposed) instead of waiting for `done`.
   GAP: the seed only accepts probe-ready segments (`MpHarvestSession.cs:95-101`) and is otherwise released at `Complete` (`:233-236`). The only cap is `MpHarvestTimeout` = 330s (`LocalLanPlayService.cs:730`). [TODAY seed waits until done; GAP R7]
8. **Dead FB is a verdict.** A dead FB link fails in about 20s, frees its slot, and is not retried by the client.
   CURRENT: a null answer is the stream's verdict (`LocalLanPlayService.cs:406-410`) and the row is Failed at `StreamResolver.cs:283-292`. [R2 two dead FB]
9. **First play never waits on `/mp`.** A healthy row is held only by earlier non-MP rows that are still probing. An MP row still in `/mp` never holds it.
   CURRENT: `FirstPlayHoldPolicy.cs`, used at `StreamResolutionOrchestrator.cs:261-269` (#162). [R9 slow MP; R9 Czechia]
10. **Stable channel numbers.** A row takes the next channel number when it joins the pool and keeps it. MP chips are appended after first play.
    CURRENT: `StreamSwitchingService.cs:9-16`, `:89`; `FirstPlayHoldPolicy.IsReleasable`. [R3/R6 normal MP, pool order]
11. **Priority window.** After first play, no new probe starts for up to 30s (or until prewarm depth is warm); probes already in flight finish.
    CURRENT: `StreamResolutionOrchestrator.cs:247`, `:1603-1645`; `FirstPlayOpenStrategy.cs:21`; `StreamResolver.cs:98-99`, `:142-148`. [R11]
    The pause also covers MP chips that are already harvested. In the normal-MP case the chips were ready at t+14.5s but were not probed until t+34s.
12. **Prewarm and race slots.** The prewarmer and the startup race may take slots during the hold, and a startup race never waits on an in-flight `/mp`.
    - CURRENT for the first part: `UpcomingStreamPrewarmer.cs:110`, `ResolverRaceThrottle.cs:108`.
    - GAP for the second: `DrainOthersAsync` (`StreamResolutionOrchestrator.cs:1375-1377`, `ResolverRaceThrottle.cs:80`) waits for every find-phase slot, `/mp` included. For Czechia it would wait until t+111s. [TODAY drain; GAP R12]
13. **Infrastructure is Deferred.** A CDN-unreachable answer (504, connect failure, manifest 403/5xx) or a LocalService outage marks the row Deferred, not Failed. The row is not reported and never holds first play.
    CURRENT: `LocalLanPlayService.cs:1062-1072`; `StreamResolver.cs:260-279`, `:335-344`. LocalService outages are retried after 2s and 5s (`StreamResolver.cs:20`, `:522-540`) and re-queued after 5s and 15s (`StreamResolutionOrchestrator.cs:37`). [R13]
14. **Retry before switch.** A stalled stream is retried in place up to 3 times (reload the playlist, then a fresh resolve) before playback moves to the next channel. A confirmed-dead error switches at once.
    CURRENT: `PlaybackPolicy.SameStreamRecovery.cs:58`, `:191`, `:197-200`. Tested by `SameStreamRecoveryBusinessRulesTests`; this is outside the probe timeline.

## Acceptance (numbers PROPOSED, for Jon to set)

These are measured on the timeline by `Expect.*` in the harness. The constants are in `ProbeAcceptance`.

- **A1.** First play comes within **2s** of the first healthy verdict, unless an earlier non-MP row is still probing (rule 9). `FirstPlayWithinOfFirstHealthy`
- **A2.** MP chips are queued as rows within **2s** of LocalService listing them. `ChipsVisibleWithin`
- **A3.** No slot sits idle while a candidate is queued, outside the priority window: **1s** total tolerance. `NoIdleSlotWhileQueued`
- **A4.** A seed with no probe-ready chip gives up within **30s** of `POST /mp`. `SeedReleasesSlotBy`
- **A5.** A startup race finishes draining within **21s** of first play (one dead FB plus 1s). `DrainWithin`

Open question for Jon: should chips that are already harvested be allowed through the priority window (rule 11)? Today they wait up to 30s.

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
- Each GAP has a skipped `Gap_*` fact.
- `GapStillReproduces` checks that every GAP still fails today.
- TODAY cases pin the current (wrong) behaviour, so the harness is shown to reproduce the Czechia log. When a gap is fixed, delete its TODAY case and un-skip its GAP fact.

What the harness does not model:
- the orchestrator's re-queue passes for Deferred rows;
- the prewarmer, and its early release of the priority window;
- our own real-time HTTP timeouts (`CancelAfter` in `LocalLanPlayService`, 65s and 330s), which are not on a `TimeProvider`.
