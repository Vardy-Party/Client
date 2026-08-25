# Phase 1 plan — domain assemblies, shared VMs, tests

Keep Android and Windows working. Do not replace Blazor WebView here. Remaining host-adapter / PlaybackCommand / UI work: [phase-2-plan.md](phase-2-plan.md) (UI last, one frontend phase).

**Test rules (locked)** for every new `[Fact]` / `[Theory]`: `// Arrange` / `// Act` / `// Assert`; AutoFixture specimens; `_fixture.GetMock<T>()` never `new Mock<T>()`; fictional names only; test projects do **not** enable ImplicitUsings; no Python.

Do not estimate calendar time. Each step is a shippable PR.

---

## Why this exists

Domain assemblies now hold the real domain (`PlaybackPolicy`, orchestrator, `GameMatcher`, ticker policy, `AuthTokenLifetime`). Around them:

- Orchestrator (application) ctor-injects the player (infrastructure) — Windows nested session then resolves the orchestrator back out of DI.
- `MauiProgram` and Linux `App` duplicate the Core graph; Linux registers `IHomePagePresentationService` and does not use it.
- `Home.razor` still owns “never auto-play `games[0]` / resume only after a real click”.
- `ApplyPlaybackCommand` is copied three times; Linux skips several flags.

Canvas: [phase-1-canvas.md](phase-1-canvas.md).

---

## Phase 0 — Characterization (no behavior change)

Lock today’s rules in tests **before** moving types.

| Rule | Lives today | Extract / test |
|---|---|---|
| Never auto-select/auto-play `games[0]`; resume only if user initiated resolution and `CurrentGame` still set | `Home.razor` | `HomePlaybackIntent` |
| Same-game identity is Home+Away, not card index | `Home.razor` `SameGame` | same type |
| Next/Prev never marks bad; failed switch reverts once; cache→fresh once | Core playback | already; add orchestrator facts (`NoWorkingStreams`, `UserClosed`, first healthy starts play while probing continues) |
| M3U8 only via LAN LocalService | LAN + `ApiService` + `StreamResolver` | both entry points call LAN; cache TTL + rediscover |
| Linux command interpreter omits retry/report/buffering | `LinuxVideoPlayerService` | characterize Core flags; do **not** “fix Linux” in this phase |

**First PR:** `HomePlaybackIntent` + tests; `Home.razor` calls it with **no UX change**; extra orchestrator outcome tests. No DI change, no player change.

---

## Phase 1a — Shared view-models (still Blazor)

Extract `HomeShellViewModel` and `MenuViewModel` (initially in Core, then Presentation). Bind `Home.razor` / `AppMenu.razor`. Linux `MainWindowViewModel` consumes the same types instead of re-filtering `EnrichedGameService`.

**Tests first:** device-login start/poll/cancel; load games only when authenticated; flatten+filter via `IHomePagePresentationService`; start/cancel resolution; LAN warning; sign-out; league flyout commands.

This is the hinge for phase 2. It is **not** an Android XAML PR.

---

## Phase 1b — Shared composition root

`VardyParty.Hosting` `AddVardyParty()`: MAUI and Linux register only host adapters (player, auth, prefs store, loggers) plus `AddVardyPartyHttpClients()`.

**Tests:** build a `ServiceCollection` with mocked LAN/API; assert Core types match across hosts.

---

## Phase 1c — Break the DI cycle

`StartAsync(Game, IPlaybackLauncher, ct)`. Orchestrator **drops** ctor `INativeVideoPlayerService`. Windows `PlayerSession` stops resolving `IStreamResolutionOrchestrator`. Nested session stays **out of DI**.

**Tests first:** fake launcher; first healthy → launcher invoked; later healthy → pool only.

---

## Phase 1d — Split assemblies (done)

Graph is acyclic:

- **Playback**, **Ports** → Kernel
- **Catalog** → Kernel (`IGamesCatalogApi` is the catalog HTTP port; `EnrichedGameService` takes that, not `IApiService`)
- **Streaming** → Catalog → Kernel; Streaming takes `IPlaybackLauncher` and `IStreamSwitchingService` from **Ports** — not Playback, not the native player
- **Presentation** → Catalog + Kernel
- **Hosting** → Auth + Catalog + Streaming + Playback + Ports + Presentation
- **`VardyParty.Core` removed**

Per-domain tests plus `VardyParty.TestSupport` (AutoFixture / `GetMock<T>()`) — done. See `tests/VardyParty.*.Tests`.

**Auth move:** device-code + refresh HTTP into an Auth token client; MAUI/Linux keep storage + interactive/PKCE. Characterization tests before moving storage (`offline_access`, sliding refresh, `invalid_grant`).

---

## Phase 1e — One `PlaybackCommand` interpreter

`PlaybackCommandExecutor` in Playback; Android/Windows wire it with **identical** behavior; Linux grows to the same flags **after** Phase 0 tests exist.

Then: retire or route `VideoPlayer.razor` `/player/...` through the orchestrator (confirm unused on the Home click path). iOS/MacCatalyst only after the paid hosts share the interpreter.

Folder hygiene last (namespaces/`Domain` folders **inside** a domain csproj). Delete dead `GetEnrichedStreamsAsync` when tests prove no callers. Cast stub: do not promote.

---

## What we will not do in phase 1

- Rewrite Blazor Home to MAUI XAML (phase 2).
- `VardyParty.Domain` + `Application` + `Infrastructure` projects at solution level.
- Register `PlayerSession` / `NativeVideoActivity` as a DI session service.
- Merge `INativeVideoPlayerService` and `IMediaEngine`.
- Production worker deploys; Python tests; committing local `appsettings.json`, `.cookie`, `backup/`.

---

## Suggested PR sequence

1. `HomePlaybackIntent` + orchestrator characterization tests — done  
2. Shared `HomeShellViewModel` / `MenuViewModel`, Blazor + Linux bound — done  
3. `AddVardyParty` composition root — done  
4. `IPlaybackLauncher` — break DI cycle — done  
5. Kernel + Playback projects — done  
6. Catalog + Streaming (`IGamesCatalogApi` / `IApiService`) — done  
7. Auth + Presentation projects; delete Core — done  
8. Shared `PlaybackCommand` interpreter; Linux parity as a follow-up — **not in this PR**; [phase-2-plan.md](phase-2-plan.md) slice 2
