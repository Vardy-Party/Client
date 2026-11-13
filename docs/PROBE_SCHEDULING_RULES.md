# Probe scheduling rules

How the client should schedule stream probing (`GET /play` for FB rows, `POST /mp` for MP/V2 pages) against LocalService. Each rule is one sentence. **CURRENT** means the code does it today; the reference after it names the type and method that does it (method names, not line numbers, so they stay true as the files change). **GAP** means it doesn't yet. Every rule is CURRENT as of 13 Nov 2026.

Facts the rules rely on (Jon, 12 Nov 2026):
- A normal `/mp` takes about 10s for the first chip, then about 1s per extra chip. Our 330s harvest cap is only a timeout.
- A dead FB link takes about 20s to fail.

Decided (Jon, 13 Nov 2026): "/mp holds a slot only until the channel list arrives." A new harvest counts as one of LocalService's 2 sessions while the page loads, then gives the slot back and carries on in the background on the single `/mp` gate (rules 2, 4, 5).

Decided (Jon, 13 Nov 2026): "first come first served on playable streams." No row holds first play: the first row to get a healthy, playable verdict plays first (rule 9).

Decided (Jon, 13 Nov 2026): "You talk of a singular recommended stream but there can be multiple. The point of this exercise is to find the best way of getting a good stream in front of a user fastest and to have a backup stream ready. So highly recommended streams come before finding." Then: "Top tier is High confidence only", "Option B, head start then overlap", "Yes, cut the startup drain and second /play", and "medium-confidence should NOT be disregarded". So:
- High-confidence recommended rows (the top tier) come first, Medium-confidence rows (the second tier) next, then finding (Low recommendations, MP pages, FB rows). After the 12s head start, first come, first served (rules 1, 9).
- Then (Jon, 13 Nov 2026): "high can occupy 2 slots on local-service / medium only one / then fit in v1 and v2 into gaps". High rows may use both LocalService slots, Medium rows at most one, and any slot they do not use is filled by finding (V1 = FB `/play`, V2 = MP `/mp`) as it frees up. A slot a dead High row frees goes straight to the next High row, else to a Medium row (rule 1).
- Then (Jon, 13 Nov 2026): "Medium limited to one slot always", refined to "Let a second Medium use a slot only when nothing else is queued, then work on faster backups". The Medium cap holds in every pass (head start, after it, re-queue passes, CDN rechecks): a free slot goes High > first Medium > finding > second Medium, so a second Medium row takes a slot only when no finding row could, and a slot never sits idle while a Medium row waits. A running probe is never pre-empted (rule 1). While a stream plays with no backup, the cap lifts (rule 11, backup push).
- Then (Jon, 13 Nov 2026), three faster-backup trade-offs, all accepted:
  - "Skip the 30s playlist-only wait while there's no backup": while a stream plays with no backup in the pool, a playlist-only chip is handed over at once; once a backup exists the 30s wait applies again (rule 7).
  - "Use a shorter first CDN recheck while there's no backup": while the viewer has fewer than two playable streams, the first CDN-unreachable recheck waits 30s instead of 2 min; every later recheck, and the first one once a backup exists, keeps the 2/5/10-minute back-off. The row stays Deferred, never dead. Risk accepted: the 2-minute first recheck was chosen after the 31 Oct 2026 IP block, so rechecking sooner could keep a block alive or trip another one (rule 13).
  - "Let finding go before a third High": the first two High rows (running or found healthy) keep both slots and the head start; a third or later High row waits while a finding row that answers in one probe (an FB row, or an MP chip the harvest holds) could take the slot (rule 1).
- Then (Jon, 13 Nov 2026): "Give an early playlist chip that fails one more try before marking it dead", then "30s on chip testing? i think 30s is too long". A playlist-only chip handed over early (no backup yet) that fails its health check is not marked dead: it is checked once more after a configurable delay, **10s** by default (`GamesApi:EarlyChipRetryDelaySeconds`), and marked dead only if that check fails too. The retry holds no slot or MP lane while it waits and adds no switch or renumbering; a chip handed over at the normal time keeps one check (rule 7).
- And (Jon, 13 Nov 2026): "you need to make logging changes so we can get and collect data". Every scheduling event that sets these delays is logged as one `[probe-timing]` line ("Probe timing log" below), so real sessions can tune them.
- And (Jon, 13 Nov 2026): "you want the best experience for the user". The acceptance targets are what the viewer sees, in seconds from opening a match: picture time, backup time, needless switches and channel-number stability ("Acceptance" below). Every scheduling rule is there to improve those numbers.
- And: the backup is the second playable High row, then the first playable Medium row, then the first playable finding row, whatever the channel numbers; a better-tier row that turns playable later replaces a finding backup. The chips of a High (Medium) MP page are High (Medium) rows (rules 1, 11).
- First play uses the link the probe returned; a fresh `/play` only when that link genuinely cannot be reused. First play never waits for other probes: the startup drain and the second `/play` are gone (rule 12).
- The priority window pauses finding only while a backup exists that still needs warming. No backup at first play means finding carries on (rule 11).

This replaces the earlier decision to keep a 1.5s recommended head start inside the startup race (13 Nov 2026): that race code (`PlaybackUrlRacer.RaceAsync`) never had a production caller.

Timeline tests: `tests/VardyParty.Streaming.Tests/Timeline/` (see "Timeline harness" below). The case names are in brackets.

## Rules

1. **Order and tiers.** Try-order is recommended rows first (High, then Medium, then Low confidence), then every MP row, then the FB rows, each group in catalog order. With no usable recommendations, MP rows go first and the FB rows are rotated by the session salt. MP rows run in their own lane (rule 4). On top of try-order, the first pass gives recommended rows a **12s** head start in which LocalService's two slots are shared out by tier:
   - **Top tier: High.** Every High-confidence recommended row (a recommended MP page or chip counts, and so does every chip LocalService lists for a High page). High rows may use **both** slots: a slot goes to one of the **first two** High rows before anything else. The first two are the High rows running or found healthy in this pass, so a dead High row does not count and its slot goes to the next High row. A **third or later** High row (two High rows already running or healthy) waits while a finding row that answers in one probe could take the slot now (a queued FB finding row, or for the MP lane a finding chip the harvest holds), and takes the slot when none could; a new finding `/mp` page does not take its place (it holds a slot while the page loads and then needs chip probes), and waits while such a High FB row is queued.
   - **Second tier: Medium.** Medium-confidence recommended rows (and the chips of a Medium page). One Medium row at a time, in every pass of the session (the head start, after it, re-queue passes, CDN rechecks), **except** that a second Medium row takes a free slot when no finding row could take it now. Order for a free slot: the first two High rows > first Medium > finding > a later High row > second Medium (a later High row still goes ahead of Medium rows when no one-probe finding row could take the slot). "Could take it now" means: for the FB window, a queued FB finding row, an MP finding row that would `POST /mp` now, or a finding `/mp` seed in flight that, with the running FB rows, fills both LocalService slots; for the MP lane, a finding chip the harvest holds or a page that can start. Finding that is only waiting (a chip not sent yet, a page behind another page's harvest) does not hold a Medium row, so no slot sits idle while a Medium row waits. Finding that appears later never pre-empts a running Medium probe; it takes the next free slot. A slot a dead High row frees goes to the next High row, else at once to a Medium row. While a stream plays with no backup in the pool, the cap is off (rule 11, backup push).
   - **Finding: everything else** (Low recommendations, MP pages, FB rows). Finding fills every slot High and Medium do not use, from the first second: V2 (`/mp`, the MP lane) first, then V1 (FB `/play`). A finding probe never starts while one of the first two High FB rows is queued, or while a Medium FB row is queued and no Medium row runs. A finding call may ask while both slots are busy; it waits for the next slot nobody in a tier wants.
   - After the 12s: first come, first served (rule 9), in try-order (High, then Medium, then finding), still with the Medium rule above (a held second Medium row leaves its slot to finding); an MP row still waits while a better-tier FB row that may start is queued.
   - With no High or Medium recommendation, nothing changes: the MP lane starts at once beside the first FB rows.
   - The head start covers the first resolver pass only. The tiers last the session (`ResolverRaceThrottle.CurrentTiers`): Deferred re-queue passes and CDN rechecks keep the Medium cap, and the backup reads them (rule 11).
   CURRENT: `StreamTestOrderPolicy.Build`; `StreamRecommendationPolicy.SpreadDiscoveryOrder` and `IsMpLike` (#162); lanes in `StreamResolver.ResolveStreamsIncrementallyAsync` (`FillWindow`, `FillFb`, `NextMp`). Tiers: `TopTierHeadStart` (`Select`, `Duration` = 12s) holding a `StreamTiers` (chips inherit their page's tier in `StreamTiers.Inherit`, called on the seed re-queue and on chips listed later), started by `ResolverRaceThrottle.BeginTopTierHeadStart` around the first pass in `StreamResolutionOrchestrator.ResolveAsync` (which also sets the session's `ResolverRaceThrottle.CurrentTiers`, cleared by a session with no tiers) and applied by `StreamResolver` in every pass (`tiers` = the head start's or `CurrentTiers`; `HoldsFinding`, `WaitingOnHeadStart`, woken by `TopTierHeadStart.Elapsed`; during the head start `FillWindow` starts tier FB rows before the MP lane). `FindingCanTakePlace` decides a second Medium row and a third or later High row (`forLaterHigh`: one-probe finding only); the first two High rows are those running plus `highHealthy`. [R1 R11 one Medium slot past the head start; R1 re-queue pass; R1 two Medium rows and nothing else; R1 finding arrives while two Medium rows run; R1 54 rows; R1 two High healthy; R1 one slow High; R1 both High dead; R1 four High; R1 R11 High-recommended MP page; R1 one High, one Medium; R1 no High, only Medium; R1 High dead, Medium healthy; R1 a dead High row's slot; R1 R11 the chips of a High MP page; R1 no recommendations; R1 R9 one High row; R1 R9 one slow High row]
2. **Concurrency.** LocalService runs at most 2 sessions at once, before first play and after it. A session is a `/play` probe, or a new `/mp` harvest until LocalService lists the page's chips (or the seed gives up, or the POST fails or is cancelled). After that the harvest holds no slot.
   CURRENT: `ResolverRaceThrottle.MaxConcurrent` and `EnterAsync`; `/play` takes a slot in `LocalLanPlayService.ResolveM3U8UrlCoreAsync`; `/mp` takes one in `LocalLanPlayService.CallMpEndpointCoreAsync` after the `/mp` gate and gives it back in `ReleaseSlotWhenListedAsync` (chip list, first chip, harvest end, or `MpHarvestSession.ProbeReadyWait`) or in the `finally` of `ReadMpHarvestAsync` (error, cancel); FB window of 2 in `StreamResolver.FillWindow` (batch size 2 from `StreamResolutionOrchestrator.ResolveAsync`). Nothing drops this to 1 after first play: the priority window (rule 11) may pause new finding probes while a backup warms, then 2 again. [R2 two dead FB; R2 /mp holds a slot only until its chips are listed; R5 slow ten-chip page; R2 R13 /mp 500 before listing]
3. **MP single-flight.** At most one MP row is probed at a time, and LocalService runs at most one `POST /mp` at a time. A row for another MP page waits in the queue while a harvest runs (it does not sit in the MP lane), and a harvest nobody wants any more is cancelled so the next page can POST.
   CURRENT: MP lane of one in `StreamResolver.FillWindow`/`NextMp`, which asks `IMpHarvestProbe.CanStartMp`; `_mpPostGate` held from the POST until that harvest ends (`LocalLanPlayService.CallMpEndpointCoreAsync`, released in the `finally` of `ReadMpHarvestAsync`); the harvest runs on its own token and is cancelled by `ReleaseMpWant` only when every caller and resolver run that wanted the page (`WantMpPage`) was cancelled. [R3 two MP pages; R3 match switch mid-harvest]
4. **Two FB beside the harvest.** Once the page's chips are listed, two FB probes run beside the background harvest; while the page loads, `/mp` and one FB share the 2 sessions.
   CURRENT: the FB window counts FB rows only (`StreamResolver.FillWindow`), and the throttle slot rule is rule 2. Before #162: `/mp` held a window place and a throttle slot for the whole harvest, so FB rows ran one at a time for 111s in the Czechia v Spain log (12 Nov 2026, 20:12:25 to 20:14:24 UK time). [R4 two FB at once beside a listed /mp harvest; R2 /mp holds a slot only until its chips are listed]
5. **Slot release.** `/mp` gives back its MP place and its throttle slot as soon as the chip list or the first chip arrives; the harvest carries on in the background on the `/mp` gate alone.
   CURRENT: the seed answers with the chip list (`MpHarvestSession.WaitForAsync`, `SeedFallback`, `ChipList`); the resolver re-queues the seed row as its first chip (`StreamResolver.ChipListSeed`, re-queue in `ResolveStreamsIncrementallyAsync`); the slot comes back in `LocalLanPlayService.ReleaseSlotWhenListedAsync` (`MpHarvestSession.Listed`). Before: the place was held until a probe-ready chip or the end of the harvest, 111s for Czechia v Spain. [R5 slow ten-chip page; R3/R6 normal MP]
6. **Chips are rows.** Every chip LocalService lists becomes its own queued row, even when the seed has no probe-ready playlist. The catalog MP row becomes the first chip (it keeps its catalog place); the other chips are new rows, appended to the pool as they turn healthy. A finished harvest answers its chips from what it saw, so one page is POSTed once.
   CURRENT: `StreamResolver.ChipListSeed` and the re-queue in `ResolveStreamsIncrementallyAsync`; chips listed later on a yield (`EnqueueMpSiblingsNext`); a finished harvest with chips hands over the list, not null (`MpHarvestSession.SeedFallback`); a finished, fault-free harvest is reused for its page for `MpHarvestSession.VerdictMemory` (60s) so each chip reads its verdict instead of re-POSTing (`MpHarvestSession.CanReuseFor`). Before: a seed with no probe-ready chip returned null and Czechia v Spain's 10 chips were dropped; a page whose chips all missed was POSTed again per chip. [R6 slow ten-chip page; R6 every chip a miss; R9 all FB dead]
7. **Deadlines.** Nothing waits in a slot or the MP lane for LocalService. The seed gives up 30s (`MpHarvestSession.ProbeReadyWait`) after `POST /mp` with nothing listed; that is retried (2s, 5s), then Deferred and re-queued, never Failed. A chip that only has a playlist is handed over as is 30s after its playlist line (or when the harvest ends); while a stream plays with no backup in the pool it is handed over at once (Jon, 13 Nov 2026), and the 30s wait applies again once a backup exists. A chip handed over early that fails its health check gets **one retry** (Jon, 13 Nov 2026): it goes back in the queue, holding no slot and not the MP lane, and is handed over again 10s later (`GamesApi:EarlyChipRetryDelaySeconds`), or sooner if its probe-ready line arrives, even if the harvest has ended; only a second failure marks it dead, and it joins the pool (or is dead) once, on its final verdict. A chip that is listed but not sent waits in the queue, outside the MP lane, until LocalService sends it or the harvest ends, however long the harvest is silent, so a chip sent late is still probed; a direct caller that waits for it (not the resolver) fails as infrastructure after 30s with no new chip line.
   CURRENT: `MpHarvestSession.DefaultProbeReadyWait` (per session, set in the constructor); seed deadline in `MpHarvestSession.WaitForAsync` (`SeedTimedOut`); playlist-only deadline in `PlaylistWaitOver`, woken by a timer (`ScheduleWake`), skipped while `handOverPlaylistOnlyNow` (from `LocalLanPlayService`: `ResolverRaceThrottle.BackupWanted`) is true, with the resolver woken when it changes (`ResolverRaceThrottle.WaitForBackupWantedChangeAsync`); early-chip retry: `MpHarvestSession.RetryEarlyChip` (only for a chip it handed over early, once; its own timer, `IsRetryWaiting`), asked by `StreamResolver` through `IMpHarvestProbe.RetryEarlyMpChip` on a Failed health verdict, which re-queues the row unstarted instead of yielding it; `LocalLanPlayService.CanStartMp` keeps a waiting retry out of the MP lane; delay `LocalLanPlayService.EarlyChipRetryDelay` from `GamesApiSettings.EarlyChipRetryDelaySeconds` (default `DefaultEarlyChipRetryDelaySeconds` = 10); `StreamResolver.NextMp` only starts a chip the harvest can answer (`IMpHarvestProbe.CanStartMp`, `MpHarvestSession.IsChipReady`) and otherwise waits for `WaitForMpHarvestChangeAsync`, so a waiting chip never holds the MP lane; direct-wait stall deadline in `MpHarvestSession.WaitForAsync` (`ChipTimedOut`, restarted by `Progress`); harvest timeout on the same clock (`LocalLanPlayService.MpHarvestTimeout`, 330s). [R7 seed gives up; R7 a playlist-only chip; R7 playlist-only chips; R7 R11 a playlist-only chip waits 30s once a backup exists; R7 R11 an early playlist-only chip that fails gets one retry; R7 an early playlist-only chip that fails twice; R7 R11 slow ten-chip page; R7 a chip sent 50s after the chip list; R9 slow MP; R4 two FB at once beside a listed /mp harvest]
8. **Dead FB is a verdict.** A dead FB link fails in about 20s, frees its slot, and is not retried by the client.
   CURRENT: a null answer is the stream's verdict (`LocalLanPlayService.CallPlayEndpointAsync`) and the row is Failed in `StreamResolver.ResolveAndTestStreamInternalAsync`. [R2 two dead FB]
9. **First come, first served.** The first row to get a healthy, playable verdict plays first, recommended or not, MP or FB. No row holds it behind an earlier row that is still probing; try-order and the tier head start (rule 1) only decide which rows are probed first.
   CURRENT: every healthy row joins the pool the moment its verdict lands (`StreamResolutionOrchestrator.ResolveAsync`, `JoinPool`). Before #162: a healthy row was held until every earlier non-MP row had yielded (`FirstPlayHoldPolicy`, now removed). [R1 R9 one High row; R1 R9 after the head start; R9 slow MP; R9 slow ten-chip page; R9 all FB dead; R9 chip A dead]
10. **Stable channel numbers.** A row takes the next channel number when it joins the pool and keeps it (append only), so channel numbers follow verdict order.
    CURRENT: `StreamSwitchingService.AddHealthyStream`, called from `StreamResolutionOrchestrator.ResolveAsync` (`JoinPool`). [R3/R6 normal MP, pool order; fast MP; R9 first come first served]
11. **Backup and priority window.** The backup is where Next and auto-advance go from the start row (the first to play), and what the prewarmer warms first: the second playable High row, then the first playable Medium row, then the first playable finding row (lowest channel number within a tier), whatever the channel numbers. A better-tier row that turns playable later replaces a finding backup; channel numbers never change (rule 10), and from any other row Next walks them as before. A start single-use row's fresh `/play` warms the same backup beside it. **Backup push:** from first play until a second row joins the pool, the Medium cap is off, so Medium rows fill every free slot ahead of finding (High rows already do); picture time is unaffected because it applies only after first play. For 30s from first play the window pauses new browser probes (`/play` or a new `/mp`) only while a backup exists that the prewarmer still has to warm (a Referer-soak link, not single-use, not warm yet). No backup, or a backup that needs no warm (a reusable link, an MP chip, a single-use link that is never warmed), means finding carries on. Probes already in flight finish. MP chips the running harvest already holds need nothing from LocalService and are probed during the window.
    CURRENT: backup push in `ResolverRaceThrottle.BackupWanted`, set from `StreamResolutionOrchestrator` `JoinPool` (`SetBackupWanted`: a row plays and the pool has fewer than 2 rows; cleared when the session ends) and read by `StreamResolver.HoldsFinding`; backup order in `PlaybackUrlReusePolicy.UpcomingInBackupOrder` (`StreamTier`); the pool applies it while the start row plays (`StreamSwitchingService.SetBackupPreference`, `GetUpcoming`, and `BackupIndex` in `SwitchToNextStream`, `GetNextHealthyStream`, `WouldWrapOnNext`), set from `StreamResolutionOrchestrator` `StartPlaybackChain` with the session's `StreamTiers`; the prewarmer reads `IStreamSwitchingService.GetUpcoming` (`UpcomingStreamPrewarmer.TickAsync`) and the fresh start's partner uses the same order (`PlaybackRacePolicy.PickPartner`). Window: `StreamResolutionOrchestrator.BeginPlaybackPriorityWindow`, `UpdatePlaybackPriorityHold`, `RunPlaybackPriorityWindowAsync`, `EndPlaybackPriorityWindow`; `FirstPlayOpenStrategy.PlaybackPriorityHolds` and `CountBackupsNeedingWarm` over `GetUpcoming` (the prewarmer's own test: `PlaybackUrlReusePolicy.ShouldPrewarm`, `NeedsPrewarmRefresh`); only ready chips while it holds (`StreamResolver.NextMp(readyOnly)`), woken by the hold ending or a harvest change (`WakeAsync`); readiness from `IMpHarvestProbe.IsMpChipReady` (`MpHarvestSession.IsChipReady`). Before: the window paused finding for up to 30s after every first play, even with nothing to warm. [R11 the priority window holds only while the backup warms; R11 harvested MP chips; R11 R12 single healthy row; R1 one High, one Medium; R11 an early FB row is the backup only until the second High row; R11 an early FB row is the backup only until the Medium row; R1 R11 the chips of a High MP page; R1 R11 High-recommended MP page]
12. **First play.** First play opens the start row (the first healthy row) on the link its probe returned, with no second `/play` and no wait for other probes. Only a link that genuinely cannot be reused gets one fresh `/play`: no link, a single-use source (cookie-bound session or a registered single-use host), or a readable token that has already expired. That fresh `/play` takes the next free slot ahead of finding (new finding starts pause while it runs; nothing is drained) and the backup is warmed beside it. A probed link that fails to open is re-`/play`ed once. The prewarmer and that start `/play` may take slots while finding is paused.
    CURRENT: `FirstPlayOpenStrategy.PlanStart` (`StartKind.PlayProbedLink`, `CanPlayProbedLink`) in `StreamResolutionOrchestrator.RaceStartAsync`; the pause is taken in `StartPlaybackChain` (`NeedsFreshStart`, `ResolverRaceThrottle.BeginRace`) and released when the start is decided; the failed-start retry is in `PlayStreamAsync`; `UpcomingStreamPrewarmer.TickAsync` and the start legs use `ResolverRaceThrottle.MarkRaceLeg`. Before: `RaceStartAsync` drained every running probe (`ResolverRaceThrottle.DrainOthersAsync`, up to a dead FB's 20s, or 30s for a loading page) and then made a second `/play` of the start row before opening it. `DrainOthersAsync` and `PlaybackUrlRacer.RaceAsync` (with its 1.5s `PlaybackRacePolicy.RecommendedGrace`) have no production caller now. [R11 R12 single healthy row; R11 the priority window holds only while the backup warms; R1 two High healthy]
13. **Infrastructure is Deferred.** A CDN-unreachable answer (504, connect failure, manifest 403/5xx), a LocalService outage, an `/mp` harvest that fails or stalls, or a cancelled shared harvest marks the row Deferred, not Failed. The row is not reported and never holds first play. A CDN-unreachable row is rechecked after 2 min, 5 min, then every 10 min while the game is live; while the viewer has fewer than two playable streams the first recheck waits 30s instead (Jon, 13 Nov 2026: long enough for a connection reset or an edge blip to clear, and only one extra request per row per session; the risk accepted is that the 2-minute wait was chosen after the 31 Oct 2026 IP block).
    CURRENT: `StreamResolver.ResolveAndTestStreamInternalAsync` and the health verdict after it. LocalService outages (and the `/mp` deadlines, rule 7) are retried after 2s and 5s (`StreamResolver.InfrastructureRetryDelays`, `ResolveWithInfrastructureRetryAsync`) and re-queued after 5s and 15s (`StreamResolutionOrchestrator.DeferredRequeueDelays`). CDN rechecks: `CdnReachabilityPolicy.RecheckDelay(attempt, noBackup)` (`FirstRecheckWithoutBackup` = 30s, `RecheckDelays`) in `CdnUnreachableRecheckLoop`, with `noBackup` from the orchestrator's pool count; the pool-row recheck after the player gives up keeps the plain back-off. A harvest that already failed answers its chips with `LocalServiceUnavailableException.FromFinishedHarvest` (not retried in place, straight to Deferred). An `OperationCanceledException` that is not the caller's own cancellation becomes a retryable `LocalServiceUnavailableException` (`StreamResolver.ResolveM3U8ResponseInternalAsync`). [R13; R11 R13 CDN recheck with a backup in the pool; R1 re-queue pass; R7 seed gives up; R13 harvest 500s midway; R2 R13 /mp 500 before listing]
14. **Retry before switch.** A stalled stream is retried in place up to 3 times (reload the playlist, then a fresh resolve) before playback moves to the next channel. A confirmed-dead error switches at once.
    CURRENT: `PlaybackPolicy.ShouldRetrySameStream`, `NextSameStreamRetryStep`, `ClassifyPlaybackError`. Tested by `SameStreamRecoveryBusinessRulesTests`; this is outside the probe timeline.

## Acceptance: the viewer's experience (numbers PROPOSED, for Jon to set)

Jon (13 Nov 2026): "you want the best experience for the user". So the targets are what the viewer sees, measured on every scenario's timeline in seconds from opening the match (t+0). The rules above exist to make these numbers better; if a rule makes one worse in some scenario, the rule changes. Constants are in `ProbeAcceptance`; checks are `Expect.*`; `ProbeSchedulingRulesTests.Acceptance` applies them to every scenario in `ProbeSchedulingScenarios.All`.

1. **Picture time**: when the first playable stream is on screen (first play). Target: within **2s** of the first healthy verdict, plus the one fresh start `/play` when the probed link cannot be reused (rule 12). `FirstPlayWithinOfFirstHealthy` (was A1). How early the first healthy verdict comes is the job of the supporting measures below.
2. **Backup time**: when a second playable stream is ready to switch to (the backup, rule 11: no warm needed, or its warm `/play` has answered). Target: within **2s** of the second healthy verdict (or of first play, if that is later), plus any warm `/play` the prewarmer ran in between. A better-tier row that replaces the backup later does not reset it. `BackupReadyWithinOfSecondHealthy`.
3. **Needless switches**: **0**. The playing stream never changes unless the viewer asks or it dies; a stalled stream is retried in place before switching (rule 14). The harness counts any change of the playing row. `NoNeedlessSwitch`.
4. **Channel-number stability**: **0** renumberings. A row keeps its channel number from the moment it joins the pool (rule 10); the backup choice never moves numbers. `ChannelNumbersStable`.

Supporting measures, folded under those (they keep the picture and backup early):
- Under picture and backup time:
  - **A2.** MP chips are queued as rows within **2s** of LocalService listing them. `ChipsVisibleWithin`
  - **A3.** No slot sits idle while a candidate is queued, outside priority-window holds and tier holds (the head start and the Medium cap): **1s** total tolerance. `NoIdleSlotWhileQueued`
  - **A4.** A seed with no probe-ready chip gives up within **30s** of its `POST /mp` (a page that waited for a slot is measured from when it got one), so a stuck page never holds a slot. `SeedReleasesSlotWithinOfPost`
  - **A5.** With High or Medium recommendations, during the **12s** head start no finding probe starts ahead of a High row or the first Medium row (rule 1). `TopTierHeadStartHeld`. Per case: `MediumRunningNeverExceeds(1)` (in every pass), `BackupReplaced`, `HighTierIncludes`.
- LocalService load (rules 2-3), per case: `LocalServiceSessionsNeverExceed`, `SessionsAtMostUntilListed` (at most 2 sessions until the chip list), `MpHoldsNoSlotFrom`, `NoSlotHeldAtEnd`, `PlaysBesideHarvestAtLeast` (2 FB beside the background harvest), `MpPostsAtOnceNeverExceed(1)`, `MpPostCount`.

Settled (Jon, 12 Nov 2026): chips that are already harvested may be probed during the priority window (rule 11).

One known miss: `SeedSilent` A2. LocalService lists the chips at t+34s, after the seed gave up at t+30s, and the resolver's retry asks again at t+37s (3s, not 2s). Picture and backup time are not affected beyond that.

### Every scenario, in seconds from opening the match

Generated by the harness: `ProbeSchedulingRulesTests.UxTable_MatchesTheRulesDoc` runs every scenario and fails when this table and the timelines disagree (it prints the table to paste in). "Picture" is first play; "Backup ready" is when a backup was first ready to switch to; "First/Second healthy" are the first two distinct healthy verdicts, the floor for each. "-" means it never happened (no healthy row, or only one). Picture later than first healthy means a fresh start `/play` (`SingleHealthyNoBackup`); backup later than second healthy means a warm `/play` (`PriorityWindow`).

<!-- ux-table:begin -->
| Scenario | Picture | First healthy | Backup ready | Second healthy | Switches | Renumbered |
|---|---|---|---|---|---|---|
| SlowTenChipPage | 13.0 | 13.0 | 19.6 | 19.6 | 0 | 0 |
| BuriedMp54Rows | 9.0 | 9.0 | 10.0 | 10.0 | 0 | 0 |
| NormalMp | 4.0 | 4.0 | 11.0 | 11.0 | 0 | 0 |
| SlowMpHealthyFb | 6.0 | 6.0 | 61.0 | 61.0 | 0 | 0 |
| MpPendingDeadFbs | 101.0 | 101.0 | - | - | 0 | 0 |
| DeadFbsOnly | - | - | - | - | 0 | 0 |
| PriorityWindow | 6.0 | 6.0 | 15.0 | 8.0 | 0 | 0 |
| CdnUnreachableFirst | 9.0 | 9.0 | - | - | 0 | 0 |
| SeedNeverProbeReady | 40.0 | 40.0 | - | - | 0 | 0 |
| SeedSilent | 38.0 | 38.0 | 39.0 | 39.0 | 0 | 0 |
| AllFbDeadWorkingMp | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| AllFbDeadFirstChipDead | 12.0 | 12.0 | 13.0 | 13.0 | 0 | 0 |
| FastMp | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| PlaylistOnlyMp | 6.0 | 6.0 | 11.0 | 11.0 | 0 | 0 |
| AllChipsMiss | - | - | - | - | 0 | 0 |
| TwoMpPages | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| HarvestFaultsMidway | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| MpErrorsBeforeListing | - | - | - | - | 0 | 0 |
| ChipLateAfterListing | 10.0 | 10.0 | 59.0 | 59.0 | 0 | 0 |
| RecommendedMpBeforeSlowFb | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| FbBeforeSlowRecommended | 6.0 | 6.0 | 26.0 | 26.0 | 0 | 0 |
| TwoHighHealthy | 9.0 | 9.0 | 10.0 | 10.0 | 0 | 0 |
| OneHighSlow | 11.0 | 11.0 | 12.0 | 12.0 | 0 | 0 |
| HighsDead | 26.0 | 26.0 | 31.0 | 31.0 | 0 | 0 |
| FourHighs | 10.0 | 10.0 | 14.0 | 14.0 | 0 | 0 |
| RecommendedMp | 4.0 | 4.0 | 11.0 | 11.0 | 0 | 0 |
| SingleHealthyNoBackup | 11.0 | 6.0 | - | - | 0 | 0 |
| NoRecs | 4.0 | 4.0 | 11.0 | 11.0 | 0 | 0 |
| HighPlusMedium | 9.0 | 9.0 | 11.0 | 11.0 | 0 | 0 |
| NoHighOnlyMedium | 7.0 | 7.0 | 10.0 | 10.0 | 0 | 0 |
| HighDeadMediumHealthy | 6.0 | 6.0 | 16.0 | 16.0 | 0 | 0 |
| EarlyFbBeforeLateHigh | 9.0 | 9.0 | 13.0 | 13.0 | 0 | 0 |
| EarlyFbBeforeLateMedium | 9.0 | 9.0 | 13.0 | 13.0 | 0 | 0 |
| DeadHighSlotToMedium | 10.0 | 10.0 | 14.0 | 14.0 | 0 | 0 |
| HighMpChips | 10.0 | 10.0 | 12.0 | 12.0 | 0 | 0 |
| MediumCapAfterHeadStart | 27.0 | 27.0 | 33.0 | 33.0 | 0 | 0 |
| MediumCapInReQueuePass | 33.0 | 33.0 | 65.0 | 65.0 | 0 | 0 |
| MediumOnlyNoFinding | 6.0 | 6.0 | - | - | 0 | 0 |
| FindingArrivesMidProbe | 40.0 | 40.0 | 46.0 | 46.0 | 0 | 0 |
| PlaylistOnlyWithBackup | 4.0 | 4.0 | 8.0 | 8.0 | 0 | 0 |
| CdnRecheckWithBackup | 4.0 | 4.0 | 7.0 | 7.0 | 0 | 0 |
| EarlyChipRetryPasses | 4.0 | 4.0 | 24.0 | 24.0 | 0 | 0 |
| EarlyChipRetryFails | 4.0 | 4.0 | - | - | 0 | 0 |
<!-- ux-table:end -->

**What sets the slowest backups** (13 Nov 2026). There is no throttle-down to one slot after first play, and the priority window holds only while a backup still needs warming, so most backup times are set by the scripted LocalService answers: SlowMpHealthyFb 61 (chip A is sent at 60), ChipLateAfterListing 59 (chip B at 58), FbBeforeSlowRecommended 26 (the recommended row turns healthy at 25), HighsDead 31 and HighDeadMediumHealthy 16 (the rows that could back up wait for a slot dead High rows hold). The levers, all done:
- **Backup push** (rule 11): Medium rows ignore the Medium cap while a stream plays with no backup. MediumCapAfterHeadStart backup 46 to 33.
- **Playlist-only chips at once while no backup exists** (rule 7): SlowTenChipPage backup 29 to 19.6 (chip Alpha, the catalog MP row), PlaylistOnlyMp 13.5 to 11.
- **A 30s first CDN recheck while no backup exists** (rule 13): MediumCapInReQueuePass backup 155 to 65.
- **Finding before a third High** (rule 1): FourHighs backup 14 (f1) instead of 17 (h3); h3 still becomes the backup at 21 (a better tier replaces a finding backup, rule 11).
No picture time changed and no switch or renumbering was added.

**Early-chip retry** (rule 7, 13 Nov 2026): no existing scenario changed. In EarlyChipRetryPasses chip A (handed over early at 12s, dead at 13s) passes its retry at 24s and is the backup; before, it was dead at 13s and there was no backup. In EarlyChipRetryFails it is dead at 24s instead of 13s, and nothing else waited on it.

## Probe timing log

Every event that sets the scheduling delays is one Information-level log line, so a session's timings can be collected from Android logcat (`adb logcat | grep probe-timing`) or the Windows log and used to tune the delays (the early-chip retry, rule 7's 30s, the head start):

```
[probe-timing] event=NAME ms=N key=value key="value with spaces" ...
```

- `ms` counts from the start of the session (`StreamResolutionOrchestrator.ResolveAsync`, `ResolverRaceThrottle.Timing.BeginSession`).
- Fields with no value are left out. URLs are reduced to their host (`page=`, `cdn=`); no path, query or token is ever logged.
- `tier` is `high`, `medium`, `finding`, or `none` (no recommendations this session).

| Event | Written by | Fields |
|---|---|---|
| `session-start` | orchestrator | `candidates` |
| `slot-grant`, `slot-release` | resolver (`lane=fb` or `mp`); `LocalLanPlayService` (`lane=mp-seed`, the LocalService slot a new `/mp` holds until its chips are listed) | `lane tier row chip page fb_active mp_active` (counts including this grant); release: `held_ms result` (`Healthy`, `Failed`, `Deferred`, `chips-listed`, `retry-queued`) or `reason` (`listed`, `timeout`) |
| `mp-chips` | `LocalLanPlayService` | `page count chips` (the page listed its chips) |
| `chip-listed` | `LocalLanPlayService` | `chip page playlist probe_ready` (a chip line arrived) |
| `chip-handover` | `LocalLanPlayService` | `chip page handover playlist cdn`; `handover` is `probe-ready`, `early` (playlist-only, no backup), `playlist-wait` (playlist-only after 30s), `harvest-end`, `miss` or `retry` |
| `health-start`, `health-end` | resolver | `row chip cdn handover early retry`; end: `result duration_ms` |
| `chip-retry-queued` | resolver | `row chip tier page first_result` (an early chip failed; one retry queued) |
| `verdict` | resolver | `row chip tier lane status reason handover after_retry cdn` (`reason`: the defer reason or health status, `no-playlist`, `error`) |
| `picture` | orchestrator | `row chip tier cdn` (the player opened on the first row) |
| `backup-ready` | orchestrator | `playing_tier backup_tier backup_row backup_chip picture_ms since_picture_ms` |
| `switch` | orchestrator | `from_tier to_tier to_row to_chip switches` |
| `session-end` | orchestrator | `picture_ms backup_ms switches playing_tier backup_tier healthy` |

Sample (EarlyChipRetryPasses, from `ProbeTimingLog_EarlyChipRetry_EmitsTheKeyLines`; the harness has no orchestrator, so no session lines):

```
[probe-timing] event=chip-listed ms=12000 chip=A page=mp.example.test playlist=true probe_ready=false
[probe-timing] event=slot-grant ms=12000 lane=mp tier=none row=A chip=A page=mp.example.test fb_active=1 mp_active=1
[probe-timing] event=chip-handover ms=12000 chip=A page=mp.example.test handover=early playlist=true cdn=cdn.example.test
[probe-timing] event=health-start ms=12000 row=A chip=A cdn=cdn.example.test handover=early early=true retry=false
[probe-timing] event=health-end ms=13000 row=A chip=A cdn=cdn.example.test handover=early result=ManifestNotFound duration_ms=1000
[probe-timing] event=slot-release ms=13000 lane=mp tier=none row=A chip=A held_ms=1000 result=retry-queued
[probe-timing] event=chip-retry-queued ms=13000 row=A chip=A tier=none page=mp.example.test first_result=ManifestNotFound
[probe-timing] event=slot-grant ms=23000 lane=mp tier=none row=A chip=A page=mp.example.test fb_active=0 mp_active=1
[probe-timing] event=chip-handover ms=23000 chip=A page=mp.example.test handover=retry playlist=true cdn=cdn.example.test
[probe-timing] event=health-start ms=23000 row=A chip=A cdn=cdn.example.test handover=retry early=false retry=true
[probe-timing] event=health-end ms=24000 row=A chip=A cdn=cdn.example.test handover=retry result=Healthy duration_ms=1000
[probe-timing] event=verdict ms=24000 row=A chip=A tier=none lane=mp status=Healthy handover=retry after_retry=true cdn=cdn.example.test
```

CURRENT: `ProbeTimingLog` (one per `ResolverRaceThrottle`, `Timing`); `ProbeSessionTrace` (picture, backup, switches via `IStreamSwitchingService.CurrentStreamChanged`, summary on dispose). Tests: `ProbeTimingLogTests`, `ProbeSchedulingRulesTests.ProbeTimingLog_EarlyChipRetry_EmitsTheKeyLines`, and `Expect.ProbeTimingLogged` (prefix, fields, no URL).

## Timeline harness

`ProbeTimelineHarness` runs the real scheduling code:
- `StreamTestOrderPolicy`
- `StreamResolver`
- `LocalLanPlayService`, including the `/mp` NDJSON harvest and `MpHarvestSession`
- `ResolverRaceThrottle`
- `StreamSwitchingService` (rows join the pool on their verdict, as in `StreamResolutionOrchestrator`)
- `TopTierHeadStart` (and its `StreamTiers`) around run 1, `FirstPlayOpenStrategy` for how first play opens and when the priority window holds, and the pool's backup order (`SetBackupPreference`, `GetUpcoming`)

Modelled like the orchestrator: the start `/play` when the probed link cannot be reused (with the backup warmed beside it), a prewarmer that warms upcoming rows that need it one at a time from first play, the rule-11 window, and (with `ThenReResolveDeferredAfter`) a re-queue pass of the rows that ended Deferred, in the same session, with no head start but the session's tiers. An FB row can answer its later `/play`s differently (`Fb(id, first, retry:)`). A row's `/play` answer can carry a plain link, a Referer soak link (`Play.SoakAt`) or a cookie-bound single-use link (`Play.SingleUseAt`). The timeline records the head start, the tiers at the end (inherited chips included), how first play opened, the fresh start `/play`, every backup in turn, when a backup was first ready and when the last one was ready, each priority hold, any change of the playing row (switches) and any renumbering of the pool.

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
- `Acceptance` runs the four experience targets and A2-A5 on every scenario in `ProbeSchedulingScenarios.All`; `UxTable_MatchesTheRulesDoc` checks the table above.
- A new rule the code doesn't do yet goes in `Gaps` with a skipped `Gap_*` fact (`NoOpenGaps` fails while any is listed, as a reminder). There are none now.

The harness records the resolver's two lanes (FB window, MP lane) from its "Starting resolve" logs and yields; A3 counts time when a lane has a free place and a queued row for it, outside the times the resolver reports a queued row waiting on a tier rule (`TopTierHeadStart.FindingHeld`, `StreamTiers.RowsHeld`). A scenario can switch match mid-run (`ThenSwitchAt`): run 1 is cancelled and run 2 starts on the same `LocalLanPlayService`; the scripted LocalService stops a `/mp` when the client closes the connection.

What the harness does not model:
- the orchestrator's re-queue timing: a scenario picks one delay for one re-queue pass (the orchestrator waits 5s/15s for an outage and 2 min, 5 min, then 10 min for a CDN-unreachable row), or with `ThenCdnRecheckDeferred` the first CDN recheck from `CdnReachabilityPolicy.RecheckDelay` (30s with fewer than two rows in the pool, else 2 min); later rechecks are not modelled;
- the player itself: first play is the moment the player would open, and a failed open is not modelled;
- the `/play` timeout (65s), which is not on a `TimeProvider`. The `/mp` harvest timeout (330s) and the 30s deadlines are on `LocalLanPlayService.Time`.

Known limits of the MP rules:
- Labels LocalService reveals only after the seed handed over its list are added when a chip row yields; a label that first appears after the last chip row yielded is not added.
- A different MP page waits for the running harvest to end (`_mpPostGate`): LocalService is single-threaded for `/mp`. Its rows wait in the queue, not in the MP lane.
- A listed chip LocalService never sends keeps the resolver run open (queued, holding no slot or lane) until the harvest ends, at most `LocalLanPlayService.MpHarvestTimeout` (330s).
