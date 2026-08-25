# Phase 1 canvas — domain assemblies + Clean Architecture

**In scope:** redistribute `VardyParty.Core`, shared view-models, acyclic composition.  
**Out of scope:** replacing Blazor WebView with XAML — see [phase-2-webview-xaml.md](phase-2-webview-xaml.md).

---

## 1. Target assemblies (vertical slices)

Domain projects, not `*.Domain` / `*.Application` / `*.Infrastructure` solutions. Layers live **inside** these.

```mermaid
flowchart BT
  Kernel["VardyParty.Kernel<br/>shared models + ports language"]
  Auth["VardyParty.Auth"]
  Catalog["VardyParty.Catalog"]
  Streaming["VardyParty.Streaming"]
  Playback["VardyParty.Playback"]
  Presentation["VardyParty.Presentation<br/>shared view-models"]
  Hosting["VardyParty.Hosting<br/>AddVardyParty()"]
  Maui["VardyParty — MAUI host"]
  Linux["VardyParty.Linux — Avalonia host"]

  Auth --> Kernel
  Catalog --> Kernel
  Catalog --> Auth
  Streaming --> Kernel
  Streaming --> Auth
  Streaming --> Catalog
  Playback --> Kernel
  Presentation --> Kernel
  Presentation --> Auth
  Presentation --> Catalog
  Presentation --> Streaming
  Presentation --> Playback
  Hosting --> Auth
  Hosting --> Catalog
  Hosting --> Streaming
  Hosting --> Playback
  Hosting --> Presentation
  Maui --> Hosting
  Linux --> Hosting
  Maui --> Playback
  Linux --> Playback
```

**Hard rule:** Streaming must not reference Playback. Playback must not reference Streaming. Presentation wires “start this game” via `IPlaybackLauncher` (method-injected), not a singleton player inside the orchestrator.

`VardyParty.Core` is deleted. Types live in the domain assemblies above. Clean Architecture rings are folders **inside** each domain csproj (`Domain/`, `Application/`, `Infrastructure/`), not extra solution projects.

---

## 2. What each assembly owns

| Assembly | Domain | From today’s Core (and new types) |
|---|---|---|
| **Kernel** | Shared language | `Game`, `EnrichedStream`, stream/playback DTOs, config POCOs, `ApiSystemDownException`. No I/O, no DI graph. |
| **Auth** | Identity | `AuthTokenLifetime`, `IAuthLoginService`, `IAuthTokenProvider`, `Auth0ApiTokenHandler` |
| **Catalog** | What matches exist | `EnrichedGameService`, `HomePagePresentationService`, BBC parsers/services, `GameMatcher`, league filter/logos, display helpers, `ScoresTickerPolicy`, `TickerMarquee` |
| **Streaming** | Get a playable URL | Resolver, expander/dedup/orderer, orchestrator, coordinator, LAN LocalService client, stream/M3U8 HTTP (`IApiService` : `IGamesCatalogApi`), health probe I/O |
| **Playback** | Play and recover | `PlaybackPolicy`, `PlaybackSessionController`, `IMediaEngine`, `INativeVideoPlayerService`, `StreamSwitchingService` |
| **Presentation** | Shared VMs | `HomeShellViewModel`, `MenuViewModel`, `HomePlaybackIntent` (`SelectionState` lives in Kernel) |
| **Hosting** | Composition | `AddVardyParty()` — only project that references every domain |

**Hosts** (`VardyParty`, `VardyParty.Linux`) implement OS adapters: Auth0 vs Linux PKCE, nested `PlayerSession` / `NativeVideoActivity` / LibVLC. Nested player sessions are **not** DI services.

Ticker **policy** stays in Catalog. Ticker **animation** stays in the host (already native on Android/Windows).

---

## 3. Clean Architecture rings (horizontal)

Same onion in every domain assembly. Arrows point inward.

```mermaid
flowchart TB
  subgraph Presentation["Presentation"]
    VM["HomeShellViewModel / MenuViewModel"]
    Blazor["Home.razor / AppMenu — stays in phase 1"]
    Axaml["MainWindow.axaml"]
    Roots["MauiProgram / Linux App"]
  end

  subgraph Application["Application — use cases"]
    HomePres["HomePagePresentationService"]
    Enrich["EnrichedGameService"]
    Orch["StreamResolutionOrchestrator"]
    Intent["HomePlaybackIntent"]
  end

  subgraph Domain["Domain — pure rules, no I/O"]
    Policy["PlaybackPolicy / PlaybackSessionController"]
    Match["GameMatcher"]
    Ticker["ScoresTickerPolicy / TickerMarquee"]
    AuthLife["AuthTokenLifetime"]
    SelectRules["expander / dedup / orderer"]
    Game["Game / EnrichedStream"]
  end

  subgraph Ports["Ports — declared next to the use case"]
    IPlay["INativeVideoPlayerService / IMediaEngine"]
    ILan["ILocalLanPlayService"]
    IApi["catalog + stream HTTP"]
    IAuth["IAuthLoginService"]
    ILaunch["IPlaybackLauncher"]
  end

  subgraph Infra["Infrastructure"]
    Api["ApiService HTTP"]
    Lan["LocalLanPlayService"]
    AuthHost["Auth0AuthService / LinuxAuthService"]
    Player["Activity / PlayerSession / LibVLC"]
    Bbc["BBC fetch + parsers"]
  end

  Presentation --> Application
  Application --> Domain
  Application --> Ports
  Infra --> Ports
  Infra --> Domain
```

| Layer | This app |
|---|---|
| **Domain** | Matcher, playback policy/session, ticker policy, token lifetime math, stream expand/dedup/order. No `HttpClient`, no Activity/WinUI. |
| **Application** | Load the card grid, start resolution, fill the healthy pool, click-vs-resume intent. |
| **Infrastructure** | HTTP, LAN, BBC fetch, Auth0/Linux stores, ExoPlayer/WinUI/LibVLC. |
| **Presentation** | Shared VMs, Blazor/Avalonia, composition roots. |

Do **not** add `VardyParty.Domain.csproj` + `Application` + `Infrastructure` at solution level — that cuts across Catalog/Streaming/Playback and recreates a god Core.

---

## 4. Match playback (target)

```mermaid
sequenceDiagram
  participant UI as Home.razor / Linux view
  participant VM as HomeShellViewModel
  participant Orch as StreamResolutionOrchestrator
  participant Pool as StreamSwitchingService
  participant Host as Nested PlayerSession / Activity
  participant SM as PlaybackSessionController

  UI->>VM: user picks a match
  VM->>Orch: StartAsync(game, launcher)
  Orch->>Orch: order + resolve M3U8 via LAN
  Orch->>Pool: AddHealthyStream
  Orch->>Host: launcher.PlayVideoAsync
  Note over Host: Nested session — not a DI singleton
  Host->>SM: engine facts
  SM->>Host: PlaybackCommand
```

---

## 5. Phase 1 UI hosts

```mermaid
flowchart LR
  HVM["HomeShellViewModel"]
  MVM["MenuViewModel"]
  Blazor["Blazor WebView<br/>Android + Windows"]
  Axaml["Avalonia XAML<br/>Linux"]
  Native["Native player chrome<br/>already not Blazor"]

  HVM --> Blazor
  HVM --> Axaml
  MVM --> Blazor
  MVM --> Axaml
  Native -.-> Playback["VardyParty.Playback"]
```

Android/Windows home stays Blazor until **phase 2**. Player chrome and scores ticker are already native.
