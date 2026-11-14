# Playback uniformity plan

Date: 14 Nov 2026 (Europe/London). Branch: `fix/mp-after-recommended-order`.
Status: **Phases 3 and 4 are implemented on #162** (this commit). **Phases 1 and 2 (normalized `PlayerEvent` / `PlaybackSupervisor`, and stall / behind-live policy) are a follow-up PR** — do not start them on this branch.

Jon (t232u / t233u): platform-specific recovery “could be a problem”; the goal is a uniform application. This plan inventories every place Android (ExoPlayer / `NativeVideoActivity`), Windows (AMS / `MediaPlayerElement`), and Linux (LibVLC + loopback proxy) still make their own recovery or policy decisions, and proposes one shared supervisor that decides identically from normalized player events.

Apple (`AppleVideoPlayerServiceBase`) is noted where it diverges, but Vardy Party does not ship there.

## Already shared (do not redo)

These already run the same policy on Android, Windows, and Linux (thin adapters only):

| Area | Shared type | Notes |
|---|---|---|
| Mid-play playlist retry (option 1) | `IPlaylistReloadRecoveryStrategy` + `PlaylistReloadCoordinator` + `PlaylistReloadRequest.MidPlay` | No last-good playlist. Shared `PlaylistReloadHoldBudget` (2s) and `PlaylistReloadGiveUpBudget` (12s). |
| Error classification after a message is built | `PlaybackPolicy.ClassifyPlaybackError` | Includes 5xx, connect failure, `PlaylistStuck` → `CdnUnreachable`. |
| Give-up wording after a later parse error | `PlaybackPolicy.ResolvePlayerErrorAfterPlaylistGiveUp` | Keeps the row as unreachable so option 2 can return. |
| Same-stream retry / move-on / CDN keep | `PlaybackSessionController` | Channel numbers stay stable; CDN-unreachable is never tried+failed. |
| Switch-back (option 2) | `SwitchBackSession` / `SwitchBackLoop` / `SwitchBackRunner` / `SwitchBackCommands` | Probe 5/15/30/60/120s, two good playlists, better-height immediate return, 30s backup dwell, cancel on user Next/Prev/chip. |
| Command execution | `PlaybackCommandExecutor` + `IPlaybackCommandHost` | Hosts only carry out effects. |

The gaps below are **before** that shared layer: each engine decides what to recover locally, how to phrase the error, when to call Ready, how tall the picture is, and whether to tell health.

## Target architecture

Adapters become translators only. They must not retry, seek, reattach, settle height, or choose to switch on their own.

```
  ExoPlayer / AMS / LibVLC
           │  thin adapter (fetch, attach, seek, report size)
           ▼
     PlayerEvent  (normalized)
           │
           ▼
  PlaybackSupervisor  (new; or a tightened PlaybackSessionController)
           │  one policy, one set of constants
           ▼
     PlaybackCommand / adapter request
      (RetryPlaylist, GiveUpAndSwitch, SeekLiveEdge,
       ReattachSameUrl, PublishHeight, ReportHealth)
```

### Proposed `PlayerEvent` set

Normalize at the adapter boundary (name as needed; keep payloads small and OS-free):

| Event | Payload | Typical sources |
|---|---|---|
| `PlaylistFetchFailed` | `status`, body class (empty / not `#EXTM3U` / 5xx) | MidPlay coordinator outcome, AMS playlist deferral, Linux proxy 502 |
| `PlaylistFetchOk` | (optional length) | MidPlay `Serve` |
| `SegmentFetchFailed` | `status` | Android `DownloadOutcomeReporter`, Windows `DownloadRequested` catch, LibVLC HTTP log |
| `SegmentFetchOk` | | Download success / `NotifyDownloadSuccess` |
| `Stalled` | `duration?`, `kind` (`PlaylistStuck` / generic) | ExoPlayer `PlaylistStuckException`; Linux/Windows only if an equivalent is detected |
| `BehindLive` | | Media3 error 1002; Windows/Linux only if the engine can express “fell off the window” |
| `VideoSizeChanged` | `width`, `height`, `generation?` | Android `OnVideoSizeChanged`; Windows natural/video size; Linux track size or `FormatGeneration` |
| `BufferAhead` | `ms` | ExoPlayer buffered-ahead; AMS live offset; LibVLC cache percent mapped if possible |
| `BufferingChanged` | `isBuffering` | Already a `MediaEngineEvent` |
| `Playing` / `ReadyForPicture` | | Windows `MediaPlaybackState.Playing`; Android READY+picture; Linux Playing or “playing without Playing event” |
| `EngineError` | raw text + any status the adapter could parse | Last resort when the engine has no structured code |
| `Ended` | | Existing |

The supervisor, not the adapter, classifies `EngineError` / `Stalled` / `BehindLive` and chooses: ride the buffer and retry the playlist, give up and switch (option 2), seek forward to the live edge, or reattach the same URL when the engine has no seek.

### Shared constants (one policy number each)

| Constant | Proposed shared value | Today |
|---|---|---|
| Live-edge / buffer target | `DesiredLiveOffsetSeconds` = 25s | Android + Windows sit 25s behind the edge. LibVLC has no live-offset API; `--network-caching` stays `LibVlcNetworkCachingMs` (4s desktop) because 25s would delay the first picture. WSL 12s is a test-host override only. |
| Playlist request hold | `PlaylistReloadHoldBudget` = 2s | Already shared |
| Session playlist give-up | `PlaylistReloadGiveUpBudget` = 12s | Already shared |
| Soft live recoveries before escalate | `MaxLiveHlsRecoveries` = 5 | Android + Windows honor it; Linux uses opaque `--http-reconnect` with no count |
| Picture-size settle | `PlaybackPolicy.DecodedPictureSettle` / `DecodedPictureSizeSettler.Settle` = 1s | Shared on Android, Windows, and Linux |
| No-picture after Ready | `NoVideoTimeout` = 8s | `EnableVideoConfirmation` on Android, Windows, and Linux. Windows still has a 20s attach-to-Playing AMS failsafe; the 8s clock starts at Ready/Playing |
| Health metrics period | `HealthMetricsInterval` = 30s | Android, Windows, and Linux report started / buffering / error / 30s metrics. Bitrate is never shown in the player UI |
| Android LoadControl windows | keep as engine fill around the playhead, documented as not a second live offset | 30/60/5/8s — no Windows/Linux equivalent |

Linux WSL 12s caching can stay a **test-host exception**, not a product policy fork.

## Inventory

Paths are from the Client repo root. Line numbers are as of `3caba87`.

### 1. Error classification and surfacing

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `VardyParty/Platforms/Android/NativeVideoActivity.cs` 1855– ( `OnPlayerErrorChanged` ); `NativeVideoActivity.Playback.cs` 355–356, 289–353 | Player errors go through `TryRecoverBehindLiveWindow` first. If not recovered, the message is `BuildEngineErrorMessage` then `ResolvePlayerErrorAfterPlaylistGiveUp`, then a session `Error`. Media3 codes and cause chains are in the text. |
| Windows | `WindowsVideoPlayerService.Session.cs` 626–654; `Playback.cs` 233–297, 738–833; `PlaybackPolicy.cs` 184–256 | `MediaFailed` tries soft live recover first. Otherwise `FormatWindowsMediaFailedMessage` (stamps “unsupported” on `SourceNotSupported`, but not on 403). If a segment was refused, the HTTP status is prefixed. Then `ResolvePlayerErrorAfterPlaylistGiveUp`. Segment failures also go through `NotifyDownloadFailure` with a status. A handler exception becomes the vague `"Stream error on Windows player."` |
| Linux | `LinuxVideoPlayerService.cs` 1336–1345, 1622–1708; `LibVlcPlaybackFailure.cs` 11–64 | `EncounteredError` raises `"Stream playback failed"` with **no status**. LibVLC log scraping can raise a better message once: `"LibVLC HTTP 403 ..."` or `"LibVLC adaptive demux failed ..."`. Anything else stays unclassifiable (`Transient` or similar) until a later guess. |

**Proposal:** Adapters emit `PlaylistFetchFailed` / `SegmentFetchFailed` / `EngineError(status?)`. The supervisor always runs `ClassifyPlaybackError` on a **normalized** sentence (same prefixes as today: `HTTP 503`, `CDN unreachable`, `PlaylistStuck`, refused 401/403/442). Ban host-local “recover or escalate” branches.

### 2. Engine-internal retries, reloads, and soft recover

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `NativeVideoActivity.Playback.cs` 289–353, 539+ (`AttachEngine`) | Mid-play playlist retries are already in MidPlay (shared). `PlaylistStuck` no longer reloads the `MediaSource` (escalates so the session can switch). `BehindLiveWindow` still seeks and `Prepare`s locally, incrementing `_behindLiveWindowRecoveries` against `MaxLiveHlsRecoveries`. Budget is **not** reset on READY (Bravia loop). |
| Windows | `WindowsVideoPlayerService.Playback.cs` 229–291, 218–225; `Session.cs` 160–162, 626–632 | No BLWE seek API. A `NetworkError` `MediaFailed` **reattaches** the same URL via `SoftRecoverPlaybackAsync` → `StartPlaybackAsync`, keeping the session generation. Nested failures while in flight are swallowed. Intentional `BeginAttach` resets `liveHlsRecoveries`. This can hide a dead network stream until the budget of 5 is gone, and is not the same as Android’s “seek forward, or switch on stuck.” |
| Linux | `LinuxPlatformProbe.cs` 138–140 (`--http-reconnect`); `LinuxVideoPlayerService.cs` 1336–1345 | LibVLC reconnects **inside** the engine with no count and no session event. A fatal error is a single vague `Error`. There is no soft-recover budget and no “seek to live edge.” |

**Proposal:** Soft recover is a supervisor decision with two adapter capabilities: `CanSeekLiveEdge` (Android yes; Windows/Linux usually no) and `CanReattachSameUrl`. Policy: `BehindLive` + can seek → seek forward; `BehindLive` + cannot seek → one budgeted reattach; `Stalled` / `PlaylistStuck` → **do not** reload or reattach (replay/stall risk) → give up and switch. Linux `--http-reconnect` should be treated as an engine limit: either disable it and let the supervisor decide, or leave it on and document that some reconnects will never be visible.

### 3. Behind live window / live-edge seek

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `PlaybackPolicy.cs` 140–147, 128; `NativeVideoActivity.Playback.cs` 336–345 | Error code 1002 or “BehindLiveWindow” text → `SeekToDefaultPosition()` + `Prepare()` + `PlayWhenReady`. This is a **forward** seek, not a replay. |
| Windows | `Playback.cs` 229–275 | No equivalent event. Network `MediaFailed` reattaches the source (see above), which may jump to the live edge or briefly re-request a window — not guaranteed to be seek-forward-only. |
| Linux | (none) | No behind-live detection. `--http-reconnect` / cache may stall or jump without a session event. |

**Proposal:** Only the supervisor may request `SeekLiveEdge`. Adapters that cannot seek report `BehindLive` (if detectable) or `EngineError` and accept `ReattachSameUrl` or `GiveUpAndSwitch`.

### 4. Stall detection (`PlaylistStuck` and friends)

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `NativeVideoActivity.Playback.cs` 316–323; `PlaybackPolicy.SameStreamRecovery.cs` 167+ (`IsStuckLivePlaylistFailure` in classify) | Stuck playlist is **not** reloaded (TV 257 20:20:21 froze ~8s and could replay). It escalates and classifies as `CdnUnreachable` so option 2 can run. |
| Windows | `PlaybackPolicy.cs` 184–206 | Soft recover is **any** `MediaPlayerError.NetworkError` that is not auth/unsupported. There is no stuck-playlist detector, so a freeze or frozen manifest can burn all five reattaches. |
| Linux | `LinuxVideoPlayerService.cs` 1336–1345 | No stall event. The user sees a freeze until LibVLC errors or the buffer ends. |

**Proposal:** `Stalled(kind)` is a first-class event. Shared rule: do not reload a stuck playlist; give up and switch; keep the row for option 2. Windows/Linux adapters should map the closest engine signal (repeated playlist body, buffer ahead = 0 for N seconds, or a documented LibVLC log line) to `Stalled`, not invent a third recovery.

### 5. Buffer and live offset

| Platform | File:line | Current behaviour |
|---|---|---|
| Policy | `PlaybackPolicy.cs` 102–124 | `DesiredLiveOffsetSeconds` = 25. Android LoadControl is a **separate** 30/60/5/8s window around the playhead. |
| Android | `NativeVideoActivity.Playback.cs` 520–531; `NativeVideoActivity.cs` 350–361 | `LiveConfiguration` target 25s, **min 3s**, max 40s. LoadControl uses the Android-only constants. Overlay buffer % is vs the 25s target (`Overlay.cs` 33–43). |
| Windows | `WindowsVideoPlayerService.Metadata.cs` 293–316; `Overlay.cs` 288–301 | `AdaptiveMediaSource.DesiredLiveOffset` = 25s, best-effort (some manifests reject it). `MinLiveOffset` may throw. Overlay uses the same 25s target. |
| Linux | `LinuxPlatformProbe.cs` 63–69, 133–139, 194, 234–235 | `--network-caching` and `--live-caching` are **4s** on desktop and **12s** on WSL. This is input cache, not a live-edge offset, and is not `DesiredLiveOffsetSeconds`. |

**Proposal:** Product policy is one live target: 25s. Android/Windows already try to honor it. Linux should set caching from the same constant where LibVLC will accept it (or the closest value), and document residual engine meaning (cache vs live offset). WSL 12s stays a conservative test-host override, not a second product number.

### 6. Switch-back attach

Shared decision: `SwitchBackRunner` + `SwitchBackPlaylistProbe.RestoreAndSwitchTo` (restore `ResolvedM3U8Url`, `UnusedResolvedUrl = true`, `SwitchToStream(index)`). Differences:

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `NativeVideoActivity.Playback.cs` 119–147, 42 | `FromRow(..., fallbackHeight: _videoHeight)`. Attach happens on the UI thread via the existing index subscription (`TrySwitchToCurrentStream`). |
| Windows | `WindowsVideoPlayerService.Playback.cs` 403+ (`CreateSwitchBackRunner`) | `FromRow(..., fallbackHeight: null)` — abandoned height depends on `Health.ProbedVideoHeight` only unless `NoteBackupHeight` has run. Attach is `MainThread` + index-driven `TrySwitchToCurrentStreamAsync` / `SwitchToCurrentAsync`. |
| Linux | `LinuxVideoPlayerService.cs` 963–979 | `FromRow(..., null)` as well. `RestoreAndSwitchTo` is called on the loop thread (pool is locked; index subscribers attach). |
| Apple | `IPlaybackCommandHost.ConsiderSwitchBack` default no-op | No option 2. |

**Proposal:** One `FromRow` height rule (probed height, else last settled `VideoSizeChanged`). One attach command: restore URL + switch index + `AttachCached` (already the reuse policy when `UnusedResolvedUrl` is set). Hosts only provide thread marshalling.

### 7. Overlay height settle

| Platform | File:line | Current behaviour |
|---|---|---|
| Shared formatter | `PlayerOverlayInfo.Resolution` / `ProbedResolution` (chrome already prefers player-measured, then probed; never playlist `RESOLUTION`) | Policy is shared; **when** a measured size is trusted is not. |
| Android | `NativeVideoActivity.cs` 1821–1834; `Playback.cs` 20–24, 42 | `OnVideoSizeChanged` publishes immediately (no 1s settle). `EnableVideoConfirmation()` is on. Previous stream size can flash if a size arrives before the new attach is bound (partly guarded by `IsPictureForCurrentAttach`). |
| Windows | `DecodedPictureSizeSettler.cs` 10–13 (`Settle` = 1s); `Overlay.cs` observe/tick; `Playback.cs` 318 (`StartWithoutPictureTimeout` = 20s) | AMS often flashes 1920×1080 for 100–200ms; the settler waits 1s. Ready is delayed until **Playing**, with a 20s “no picture” start failure. `EnableVideoConfirmation` is **off**. |
| Linux | `LinuxVideoPlayerService.cs` 91, 368, 900–906, 1376–1452 | Size is untrusted until `FormatGeneration` bumps after a switch. If there is no software frame sink, **Ready alone** trusts the size (can flash the previous stream). No 1s settler. No video confirmation. LibVLC often never raises Playing on the vmem path. |

**Proposal:** Every adapter emits `VideoSizeChanged`. The supervisor runs **one** settler (`DecodedPictureSizeSettler`) and one “ignore sizes from the previous attach generation” rule (Linux `FormatGeneration` / Android attach URL check / Windows settler reset). `EnableVideoConfirmation` on all platforms, timeout = `NoVideoTimeout` (8s), replacing the Windows-only 20s watchdog or making 20s a documented engine exception if 8s is too tight for AMS first frame.

### 8. Health reporting

| Platform | File:line | Current behaviour |
|---|---|---|
| Android | `NativeVideoActivity.cs` 1624–1653, 1656–1668; `Playback.cs` 265, 277 | Started, buffering, error, and **30s metrics**. Crowd-health identity is the catalog URL. |
| Linux | `LinuxVideoPlayerService.cs` 1002–1039, 1511–1537 | Started, buffering, error, and **30s metrics**. Same identity helper. |
| Windows | `WindowsVideoPlayerService.Playback.cs` 445–464 | `ReportWorking` → `ReportPlaybackStartedAsync` only. `ReportFailed` / `ReportDeclined` **log and do not call the health reporter**. No periodic metrics, no buffering health reports. |

**Proposal:** `IPlaybackCommandHost.ReportFailed` / `ReportDeclined` / `ReportWorking` / buffering always hit `IStreamHealthReporter` with the same identity helper. Periodic metrics (30s) on all platforms, including Windows.

### 9. Other host-local policy that should move

| Topic | Android | Windows | Linux | Proposal |
|---|---|---|---|---|
| Video confirmation | On (`Playback.cs` 23–24), 8s | Off; 20s start watchdog (`Playback.cs` 318–362) | Off | One flag, one timeout |
| Ready meaning | READY, but “working” waits for a picture | Ready only on **Playing** | Ready on Playing, or “playing without a Playing event” (`LinuxVideoPlayerService.cs` 1227) | Supervisor takes `Playing` / `ReadyForPicture`; adapters must not mark established early |
| Refused-segment short-circuit | `IsRefusalAlreadyHandled` (`Playback.cs` 497) | `lastDownloadFailureStatus` prefixed onto `MediaFailed` (`Session.cs` 645–647) | Log-scraped 403 only | `SegmentFetchFailed(403)` → same token path |
| Trim played segments | `PlayedSegmentMemory` + `TrimPlayedSegments` on rewritten / stall-prepare playlists | None | Proxy may rewrite playlists, no played-segment trim | Keep as an Android adapter detail unless a stall prepare still exists; never use it to serve last-good |

## Engine limits that genuinely cannot be unified

These should stay documented exceptions, not hidden if-platform branches in policy:

1. **ExoPlayer `ERROR_CODE_BEHIND_LIVE_WINDOW` (1002)** is Android-only. Windows and Linux have no BLWE signal; they can only approximate with buffer-ahead and errors.
2. **Windows AMS has no seek-to-live-edge API** comparable to `SeekToDefaultPosition`. Soft recover is reattach, and `DesiredLiveOffset` is best-effort (manifests may reject it). `DownloadRequested` must finish within `WindowsPlaylistDeferralBudget` (8s) or AMS may abandon the request — already bounded by the shared 2s hold.
3. **LibVLC `--http-reconnect` and `--network-caching` are opaque.** Caching is not a live-edge offset. `EncounteredError` often has no HTTP status. Playing may never fire on the vmem/HLS path. `--http-reconnect` can recover without the supervisor knowing.
4. **LibVLC must go through the loopback proxy** so DualStack/DoH and headers work (`LinuxHlsLocalProxy`). That is transport, not policy, and can stay.
5. **First-frame size lies:** AMS 1920×1080 flash is real; the shared settler is the fix, not a platform rule.
6. **Apple AVPlayer** is unwired for MidPlay and option 2. Out of scope until a ship decision.

## Phased plan

Jon (t234u): implement phases 3 and 4 on #162; phases 1 and 2 wait for a follow-up PR.

### Phase 0 — already on #162 (`8cfb0f0`, `06a67ef`, `3caba87`, `25b747a`)

MidPlay, no last-good, option 2, real HTTP status on Android, give-up as `CdnUnreachable`, shared 2s/12s budgets, all-caps league title fix. Keep these tests.

### Phase 1 — Normalized events at the adapter boundary (follow-up PR)

Introduce `PlayerEvent` and a `PlaybackSupervisor` (or extend `PlaybackSessionController` + `MediaEngineEvent` so hosts stop calling `TryRecover*`).

- Each adapter maps engine callbacks to `PlayerEvent` only.
- Delete host-local escalate/recover: Android `TryRecoverBehindLiveWindow` decision, Windows `TryRecoverLiveHlsFailure` decision, Linux log-to-error duplicates where the supervisor can take a structured event.
- Tests: fake player emits the same script (`PlaylistFetchFailed(503)×3`, `PlaylistStuck`, `BehindLive`, `SegmentFetchFailed(403)`) and the supervisor’s commands are identical. One script, three adapter fakes (or one fake engine plus three mapping tests).

### Phase 2 — Stall and behind-live policy in one place (follow-up PR)

Supervisor rules: seek forward only for `BehindLive` when `CanSeekLiveEdge`; otherwise one budgeted reattach; `Stalled`/`PlaylistStuck` always give up and switch (option 2). Shared `MaxLiveHlsRecoveries`. Document Linux `--http-reconnect` as an engine limit or turn it off.

Tests: timeline — behind-live then recover without switch on a seek-capable fake; stuck playlist switches and arms switch-back on all fakes; recovery budget exhausts then move-on.

### Phase 3 — Buffer target and height settle (done on #162)

Shared constants live in `PlaybackPolicy`. Android and Windows already sit `DesiredLiveOffsetSeconds` (25s) behind the live edge. LibVLC cannot express a live-edge offset: mapping 25s onto `--network-caching` would delay the first picture by about 25s, so desktop Linux keeps `LibVlcNetworkCachingMs` (4s) and WSL keeps the 12s test-host override. Overlays still treat 25s as the product target when buffer-ahead can be measured.

All three overlays feed sizes through `DecodedPictureSizeSettler` (`DecodedPictureSettle` = 1s). `EnableVideoConfirmation()` is on for Android, Windows, and Linux with `NoVideoTimeout` (8s after Ready). Windows retains a 20s attach-to-Playing AMS failsafe; the 8s no-picture clock starts at Ready/Playing.

Tests: `PlaybackUniformityParityTests`, `DecodedPictureSizeSettlerTests`, `AudioOnlyLoopGuardBusinessRulesTests`, `LinuxPlatformProbeTests.ResolveLiveNetworkCachingMs_UsesSharedPolicyConstants_AndNeverThe25sLiveOffset`.

### Phase 4 — Windows health parity (done on #162)

Windows `ReportFailed` / `ReportDeclined` call `IStreamHealthReporter.ReportPlaybackErrorAsync`. Buffering reports `ReportBufferingAsync`. A 30s `HealthMetricsInterval` tick reports `ReportPlaybackMetricsAsync` and raises session `Metrics`, matching Android and Linux. Crowd-health identity is the catalog URL. Bitrate from these samples is never shown in the player UI.

Ready/established-after-picture as a supervisor rule stays in the phase 1–2 follow-up; this phase only turns confirmation on so Ready alone is not "working" on Windows or Linux.

### Suggested shared fake-player test shape

```text
script:
  t=0     attach url-A
  t=1s    Playing + VideoSizeChanged(1920,1080)
  t=1.2s  VideoSizeChanged(1280,720)     → overlay still 1080 until settle, then 720
  t=20s   PlaylistFetchFailed(503) × 3   → MidPlay give-up, CdnUnreachable, switch to url-B, arm switch-back
  t=25s   url-A playlist 200, 200        → switch-back if height better / after dwell
  alt:    Stalled(PlaylistStuck)         → switch, never Seek/Reattach
  alt:    BehindLive                     → SeekLiveEdge if capable, else one ReattachSameUrl
  alt:    SegmentFetchFailed(403)        → token path, not ConfirmedDead
```

Run the script once against a pure supervisor fake (no OS). Then run adapter mapping tests: “given this ExoPlayer / MediaFailed / LibVLC log, the `PlayerEvent` is X.” That gives uniformity without needing a device.

## Suggested first build after this plan

Only when Jon asks for a build: the uniformity work is not in a build yet. Devices are still on earlier builds until then.
