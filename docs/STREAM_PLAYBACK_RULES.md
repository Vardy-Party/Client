# Stream Playback Rules

**STATUS:** In progress — Android `NativeVideoActivity` now executes `PlaybackSessionController` effects; Windows still owns local Recover*  
**AUDIENCE:** Developers and AI assistants working on MAUI stream handling (Android, Android TV, Windows)  
**RELATED:** [STREAM_HEALTH_PROTOCOL.md](STREAM_HEALTH_PROTOCOL.md)

### Implementation (Core + Android)

| Type | Path | Role |
|------|------|------|
| `PlaybackPolicy` | `VardyParty.Core/Playback/PlaybackPolicy.cs` | Pure rules (attach, navigate, revert, cache retry, decline) |
| `PlaybackSessionController` | `VardyParty.Core/Playback/PlaybackSessionController.cs` | State machine: engine events → effects |
| `PlaybackCommand` | `VardyParty.Core/Playback/PlaybackCommand.cs` | Collapses effect batches (remove+advance must not skip) |
| `IMediaEngine` | `VardyParty.Core/Playback/IMediaEngine.cs` | Slim OS contract (Attach/Stop/events/metrics only) |
| Effects / events | `PlaybackEffect.cs`, `MediaEngineEvent.cs` | Host executes effects; engine emits facts |
| Android host | `NativeVideoActivity.Playback.cs` | ExoPlayer facts → session → pool/health/attach |
| Tests | `tests/VardyParty.Core.Tests/Playback*.cs`, `StreamMetricsWindowTests.cs` | Table-driven policy + session + host commands |

**Next:** Wire Windows `WindowsVideoPlayerService` to the same controller; delete `RecoverFromFailed*`. Keep WinUI as attach/stop/events only.

---

## Goal

One set of business rules for stream **selection → start → survive → switch → recover**.  
OS code should only attach/detach media and surface metrics/errors. Shared Core owns decisions.

Today: selection/pre-play is largely shared; **runtime recovery is fragmented** across Android and Windows players. This document is the source of truth for unifying that.

---

## As-is architecture

```
Home.razor
  └─ StreamResolutionOrchestrator          ← shared select / start / post-PlayVideoAsync failure
       ├─ StreamSelectionCoordinator
       ├─ StreamResolver + StreamHealthChecker
       ├─ StreamSwitchingService            ← healthy pool + index Rx
       └─ INativeVideoPlayerService.PlayVideoAsync(...)
            ├─ Android (+ TV): NativeVideoActivity (ExoPlayer)  ← owns auto-switch + decline
            └─ Windows: WindowsVideoPlayerService (WinUI)     ← owns revert / auto-advance / generations
```

**Alternate path (weaker):** `VideoPlayer.razor` (`/player/...`) — single URL, no orchestrator failover pool.

Android phone and Android TV share the same ExoPlayer activity; TV differs mainly in remote/overlay focus (`MauiProgram.IsTv`, `RemoteKeyHandler`).

---

## Phase matrix (as-is vs target)

| Phase | Shared Core today | Android today | Windows today | Target (unified) |
|-------|-------------------|---------------|---------------|------------------|
| **Select / order** | Recommendations + catalog order + FB-before-MP | Uses Core | Uses Core | Keep in Core |
| **Pre-play probe** | `StreamHealthChecker` statuses | Uses Core | Uses Core | Keep in Core |
| **Skip countdown** | `StreamResolver` skips `IsCountdown` | — | — | Keep |
| **Start first healthy** | First healthy → `PlayVideoAsync`; continue testing | Attaches ExoPlayer | Attaches AdaptiveMediaSource | Keep |
| **Cache → fresh retry** | Once if cached M3U8 fails (CDN token) | N/A (orchestrator) | N/A (orchestrator) | Keep in Core |
| **Prefetch next M3U8** | `PrefetchUpcomingStreamUrl` | Index change rebinds | Index change rebinds | Keep |
| **User Next / Prev** | `SwitchToNext/Previous` + resolve if needed | Rebind URL; **must not** mark bad | Rebind; **must not** mark bad | Shared policy |
| **Hard playback error** | Only after `PlayVideoAsync` returns | Immediate auto-next via `RequestNextStream` | Established → auto-next; else close / start-fail path | **One** policy |
| **Soft decline (buffer/bitrate)** | Not used | `StreamMetricsWindow` → auto-next | **Not used** | Shared window on all OS |
| **Failed switch** | Index advance only | Stay on next if index moved | Remove broken + **revert last-good** | Decide once |
| **Failed start (never established)** | `HandlePlaybackFailureAsync`: remove + try next | Error → next (same as hard error) | Clear URL → next if pool >1 else close session | Shared |
| **Stale failure during switch** | — | Null `OnPlayerErrorChanged` ignored | Generation check ignores stale `MediaFailed` | Shared generation id |
| **Buffering → Core** | Sticky OR flag until report | `ReportBufferingState` **no-op** | Raises `BufferingStateChanged` | Always raise |
| **Health reports** | Orchestrator 30s poll + reporter | Activity timer + event reports | Orchestrator + Windows events | Single reporter path |

---

## Detailed as-is rules

### 1. Selection & pre-play (Core — keep)

1. Build candidate queue (`StreamSelectionCoordinator`); apply crowd recommendations when confidence is high/medium.
2. Expand V2 / order catalog sources; prefer FB-before-MP where applicable.
3. Skip streams with countdown active on the page.
4. Resolve M3U8 via LAN play service / API; probe with `StreamHealthChecker`.
5. Probe outcomes: `Healthy` | `ManifestUnreachable` | `InvalidManifest` | `EmptyManifest` | `SegmentUnreachable`.
6. Deduplicate healthy URLs in the switching pool.
7. Play the **first** healthy stream immediately; keep resolving others in the background.

### 2. Start & cache retry (Core — keep)

1. Prefer `ResolvedM3U8Url` from probe/prefetch.
2. If playback fails and a cached URL was used (and user did not close), resolve fresh once; retry only if the fresh URL differs.
3. Prefetch the upcoming candidate’s M3U8 while current plays.

### 3. Switch eligibility (partially shared)

Shared helper `SwitchingDecision.CanSwitch(currentUrl, candidateUrl, isPreparing)`:

- Reject empty candidate
- Reject while preparing
- Reject same URL (case-insensitive)

Android duplicates this in `NativeVideoActivity.CanSwitchTo`. Windows uses local guards (`suppressIndexDrivenSwitch`, same URL, generation). **Target:** all platforms call `SwitchingDecision` (or successor).

### 4. Android recovery (`NativeVideoActivity`)

| Trigger | Action | Removes from pool? | Reverts last-good? |
|---------|--------|--------------------|--------------------|
| ExoPlayer `OnPlayerErrorChanged` (non-null) | Report error; `TryAutoSwitchFromPlaybackError` → `RequestNextStream` / `SwitchToNextStream` | No (unless orchestrator later) | No |
| Soft decline (`StreamMetricsWindow.IsHealthDeclined`) | `RequestNextStream` | No | No |
| User Next / Prev | Index change → `TrySwitchToCurrentStream` | No | No |
| Playback ended | Clear in-memory manifest; no auto-next in listener | No | No |
| Null error clear | **Ignore** (avoids spurious switch) | — | — |

**Decline window** (`StreamMetricsWindow`, 60s buffering/bitrate window; errors over 300s):

- ≥ 4 buffering events → declined  
- ≥ 3 bitrate samples with average &lt; 300 kbps → declined  
- ≥ 10 bitrate samples and last 3 all &lt; 500 kbps → declined  
- ≥ 3 errors in 300s → declined  

**Gaps:** `AndroidVideoPlayerService.ReportBufferingState` is a no-op (orchestrator may miss buffering). Soft decline does not clear `ResolvedM3U8Url` or remove the stream. Auto-switch may race with orchestrator `HandlePlaybackFailureAsync` when the session eventually ends.

### 5. Windows recovery (`WindowsVideoPlayerService`)

| Trigger | Condition | Action |
|---------|-----------|--------|
| `MediaFailed` | Stale generation | Ignore |
| `MediaFailed` | `hasEstablishedPlayback` | `HandleActiveStreamFailureAsync` → `onNextStreamRequested` else revert last-good |
| `MediaFailed` | Not established | Close session with error (orchestrator sees failure) |
| ≥ 5 consecutive segment/manifest download failures | Established + current generation | Active failure → next |
| ≥ 5 download failures | Established + stale attach generation | `RecoverFromFailedSwitchAsync` |
| ≥ 5 download failures | Not established | `RecoverFromFailedStartAsync` |
| Attach exception during switch | Established, not revert | Remove current + clear URL + **revert last-good** |
| Attach/start failure | Never established | Clear URL; if pool &gt; 1 call next; else close session |
| User Next | Index Rx | `TrySwitchToCurrentStreamAsync` (do not double-call after `SwitchToNextStream`) |

**Notable:** Windows **reverts to `lastGoodPlaybackUrl`** after a failed switch. Android does **not** — it advances and stays on the next index. This is the largest behavioral fork.

### 6. Orchestrator post-session failure (Core)

Only runs when `PlayVideoAsync` **returns** with `Success == false` (and not “User closed”):

1. Report health `failed`
2. `RemoveCurrentStream`
3. `TryNextHealthyStreamAsync` → `PlayStreamAsync` on next healthy
4. If none left → resume selection testing

**Problem:** Android/Windows often already auto-switched inside the native player, so this path is late, incomplete, or fighting platform logic.

### 7. Health reporting identity

Protocol keys on `streamUrl` ([STREAM_HEALTH_PROTOCOL.md](STREAM_HEALTH_PROTOCOL.md)). In practice:

- Orchestrator reports often use **page/stream URL**
- Android activity reports often use **M3U8 URL**

Correlate carefully until identity is unified via `StreamHealthIdentity`.

---

## Target unified policy (proposed)

These rules should become a testable `PlaybackPolicy` / `StreamSessionController` in Core. Platforms only emit facts.

### States

`Idle → Probing → Starting → Playing → Buffering → Declining → Switching → Recovered | Failed | Closed`

### Rules (normative)

1. **Next/Prev never marks a stream bad** — only explicit user report or policy-classified hard fail / decline.
2. **Established playback** = first successful decode/ready (Android `STATE_READY` + tracks; Windows source attach + playback). Track `lastGoodUrl` + `generation`.
3. **Hard fail** (decoder/source error, or ≥ N consecutive download failures):  
   - Clear `ResolvedM3U8Url` for current  
   - Remove from healthy pool  
   - If another healthy exists → switch to next (resolve/prefetch)  
   - Else → resume probing / fail session  
   - Do **not** silently revert unless switch itself failed after leaving last-good (see 4).
4. **Failed switch** (new URL never established): restore `lastGoodUrl` **once**, keep failed candidate removed; then user/next can try again.
5. **Soft decline** (shared `StreamMetricsWindow` thresholds on all platforms): same as hard fail for pool removal + advance (tunable; start by matching Android numbers).
6. **Cache retry**: one fresh resolve if first attach used cache and failed before established.
7. **Stale events**: ignore errors whose `generation != currentAttachGeneration`.
8. **Single health reporter**: platforms push metrics/errors into Core; Core posts to API with one identity key.
9. **Buffering always raises** `BufferingStateChanged` into Core on every OS.

### Slim OS adapter surface (target)

```text
Attach(url, headers) / Stop()
Observe → Ready | Buffering | Metrics | Error(code, message) | Ended
Optional: native chrome / TV remote wiring only
```

No switch, recover, overlay stream list, or health POST logic inside platform files.

---

## Observability — AI / human “is this stream healthy?”

### Android TV / Android phone — live logs

Correct one-shot filter by process (avoid duplicating `adb logcat`):

```bash
adb logcat --pid=$(adb shell pidof -s com.vardyparty)
```

**PowerShell** (Windows host talking to device/emulator):

```powershell
$pid = adb shell pidof -s com.vardyparty
adb logcat --pid=$pid
```

Useful filters once you have the PID stream:

```bash
adb logcat --pid=$(adb shell pidof -s com.vardyparty) | rg "StreamResolution|StreamSelection|StreamHealth|NativeVideoActivity|Playback error|Switch|declin|buffer"
```

Optional tag focus:

```bash
adb logcat VardyParty:I NativeVideoActivity:I *:S
```

### Log markers to treat as a session timeline

| Marker | Meaning |
|--------|---------|
| `[StreamResolution]` | Orchestrator select/start/switch/prefetch/cache-retry |
| `[StreamSelection]` | Candidate queue / pause-resume testing |
| `[StreamHealth]` / health reporter | Crowd reports |
| `[NativeVideoActivity] Player ready` | Established on Android |
| `[NativeVideoActivity] Playback error` | Hard fail → auto-next |
| `[NativeVideoActivity] Switching player` | Rebind after index change |
| Windows `VideoPlayer` / `WindowsEventLogger` | Attach, MediaFailed, revert, generation ignore |
| `Stream failed after 5 consecutive download errors` | Windows soft→hard path |

### Healthy session (expected pattern)

1. Probe → Healthy  
2. Using cached or fresh M3U8  
3. Player ready / source attached  
4. Playback started (+ metadata)  
5. Periodic metrics without rapid error/auto-switch  

### Unhealthy session (expected pattern)

1. Ready never reached, or  
2. Playback error / MediaFailed / max download failures, then  
3. Switch / remove / revert (platform-dependent until unified), then  
4. Either Recovered (ready again) or pool exhausted  

### Target for AI agents

Emit one structured event stream (file or log JSON lines) per session:

`ProbeResult` → `PlaybackEstablished` → `MetricsSample` → `BufferingSpike` | `Declined` | `HardFail` → `SwitchRequested` → `SwitchSucceeded` | `Reverted` → `SessionEnded`

Until that exists, agents should reconstruct the timeline from the markers above and flag **Android advance-without-revert** vs **Windows revert-on-failed-switch** as known divergence—not as random bugs.

---

## Known divergence summary (fix first)

1. **Failed switch:** Windows reverts to last-good; Android advances and stays.  
2. **Soft decline:** Android only (`StreamMetricsWindow`).  
3. **Buffering into Core:** Windows yes; Android hook empty.  
4. **Orchestrator remove+next** often races native auto-switch.  
5. **Health URL key** page vs M3U8 inconsistent.  
6. **Dual entry:** Home orchestrator vs `/player` Blazor path.

---

## Implementation backlog (suggested order)

1. Freeze this doc; add unit tests for `PlaybackPolicy` decisions (table-driven).  
2. Shared session controller consuming engine events; Android calls it from ExoPlayer listener.  
3. Windows call same controller; delete duplicated Recover* locals incrementally.  
4. Fix Android `BufferingStateChanged`; one reporter path; unified streamKey.  
5. Slim `NativeVideoActivity` / `WindowsVideoPlayerService` to media + chrome.  
6. Align or retire `VideoPlayer.razor` failover behavior.  
7. Optional: dump session event JSON next to logcat for agent observation.

---

## Source map

| Concern | Primary files |
|---------|----------------|
| Orchestration | `VardyParty.Core/Orchestrators/StreamResolutionOrchestrator.cs` |
| Selection | `VardyParty.Core/Orchestrators/StreamSelectionCoordinator.cs` |
| Resolve / probe | `VardyParty.Core/Resolvers/StreamResolver.cs`, `Health/StreamHealthChecker.cs` |
| Pool / switch index | `VardyParty.Core/Services/StreamSwitchingService.cs` |
| Decline window | `VardyParty.Core/Health/StreamMetricsWindow.cs` |
| Switch guard | `VardyParty.Core/Services/SwitchingDecision.cs` |
| Android player | `VardyParty/Platforms/Android/NativeVideoActivity.cs` |
| Android bridge | `VardyParty/Platforms/Android/AndroidVideoPlayerService.cs` |
| Windows player | `VardyParty/Platforms/Windows/WindowsVideoPlayerService.cs` |
| Crowd protocol | `docs/STREAM_HEALTH_PROTOCOL.md` |
