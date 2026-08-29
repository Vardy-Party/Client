# VardyParty architecture

One shared MAUI XAML homepage (`VardyParty.HomeUi`), five heads, native players
outside the UI toolkit. Domain policy lives in plain `net11.0` assemblies and is
unit-tested without MAUI/Avalonia.

For the homepage rewrite (Blazor → XAML, Avalonia on Linux, TV focus/clip), see
[architecture/homepage-maui-avalonia.md](architecture/homepage-maui-avalonia.md).
For playback rules, see [STREAM_PLAYBACK_RULES.md](STREAM_PLAYBACK_RULES.md).

## Heads and renderers

```mermaid
flowchart LR
  subgraph Shared["Shared UI"]
    HomeUi["VardyParty.HomeUi<br/>MAUI XAML"]
  end

  subgraph Heads["App heads"]
    Maui["VardyParty<br/>Android / iOS / Mac Catalyst / Windows"]
    Desktop["VardyParty.Desktop<br/>Linux / WSL"]
  end

  subgraph Draw["How HomeUi is drawn"]
    Native["Platform MAUI handlers<br/>Android / WinUI / UIKit"]
    Avalonia["Avalonia 12 MAUI backend<br/>UseAvaloniaApp"]
  end

  subgraph Video["Native video - not in HomeUi"]
    Exo["ExoPlayer"]
    WinUI["MediaPlayerElement"]
    AV["AVPlayer"]
    VLC["LibVLC window"]
  end

  HomeUi --> Maui
  HomeUi --> Desktop
  Maui --> Native
  Desktop --> Avalonia
  Maui --> Exo
  Maui --> WinUI
  Maui --> AV
  Desktop --> VLC
```

| Head | UI | Video | Field status |
|------|----|-------|--------------|
| Android phone / **32-bit TV** | MAUI handlers | ExoPlayer | Verified |
| Windows | MAUI → WinUI | MediaPlayerElement | Verified |
| Linux / **WSL** | MAUI → Avalonia | LibVLC (separate window) | Verified |
| iOS / Mac Catalyst | MAUI → UIKit | AVPlayer | CI builds; untested pending Apple Developer Account |

## Domain assemblies (inward dependencies)

```mermaid
flowchart TB
  Heads["VardyParty / VardyParty.Desktop / HomeUi"]
  Hosting["VardyParty.Hosting"]
  Presentation["VardyParty.Presentation<br/>board differ, layout, events, back policy"]
  Playback["VardyParty.Playback"]
  Streaming["VardyParty.Streaming"]
  Catalog["VardyParty.Catalog"]
  Auth["VardyParty.Auth"]
  Ports["VardyParty.Ports"]
  Kernel["VardyParty.Kernel"]

  Heads --> Hosting
  Heads --> HomeUi["VardyParty.HomeUi"]
  HomeUi --> Presentation
  Hosting --> Auth
  Hosting --> Catalog
  Hosting --> Streaming
  Hosting --> Playback
  Hosting --> Presentation
  Playback --> Ports
  Streaming --> Ports
  Catalog --> Kernel
  Auth --> Kernel
  Presentation --> Kernel
  Ports --> Kernel
```

`VardyParty.Presentation` has **no** MAUI/Avalonia references. Heads depend
inward; policy does not depend on UI.

## Request path (pick a match → play)

```mermaid
sequenceDiagram
  participant UI as HomeUi / host page
  participant Orch as StreamResolutionOrchestrator
  participant Pool as Playback pool
  participant Ctrl as PlaybackSessionController
  participant Eng as IMediaEngine / OS player

  UI->>Orch: Start resolution for fixture
  Orch-->>UI: Progress (finding streams)
  Orch->>Pool: Healthy candidates
  UI->>Ctrl: Launch / attach
  Ctrl->>Eng: Attach URL
  Eng-->>Ctrl: Buffering / Ready / Error facts
  Ctrl->>Eng: Switch / stop effects
```

## Related docs

| Doc | Role |
|-----|------|
| [architecture/homepage-maui-avalonia.md](architecture/homepage-maui-avalonia.md) | Homepage + Linux Avalonia backend deep dive |
| [STREAM_PLAYBACK_RULES.md](STREAM_PLAYBACK_RULES.md) | Session controller / engine contract |
| [STREAM_HEALTH_PROTOCOL.md](STREAM_HEALTH_PROTOCOL.md) | Health checking protocol |
| [LINUX_SUPPORT.md](LINUX_SUPPORT.md) | .NET 11 preview + Desktop run on Linux/WSL |
| [VERSION_MANAGEMENT.md](VERSION_MANAGEMENT.md) | `Version.props` + release versioning |
