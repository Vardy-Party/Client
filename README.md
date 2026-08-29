# VardyParty

A cross-platform live match streaming app. One shared MAUI XAML homepage on every
target; native players own video. Built for **v2.0.0**.

## Highlights

- **.NET 11** — MAUI and the Linux desktop head both target `net11.0` / `net11.0-*`.
- **Avalonia MAUI backend** — on Linux, the same `VardyParty.HomeUi` XAML is drawn by
  Avalonia (`UseAvaloniaApp` / `Avalonia.Controls.Maui.Desktop`), not a second UI stack.
  See [docs/architecture/homepage-maui-avalonia.md](docs/architecture/homepage-maui-avalonia.md).
- **Video on Linux / WSL** — playback is **LibVLC in a native window**, not hosted inside
  Avalonia controls. The Desktop build runs under WSL with working video + audio.
- **32-bit Android TV** — exercised on ARM `armeabi-v7a` sets (e.g. Sony BRAVIA). D-pad
  navigation, focus, and the shared homepage are tuned for a smooth 10-foot UI.

VardyParty aggregates live streams, health-checks them, and plays with automatic failover
when a feed dies.

## Tech Stack

- **.NET 11** (preview) — shared libraries + MAUI hosts + Linux desktop
- **.NET MAUI** with a shared **MAUI XAML homepage** (`VardyParty.HomeUi`)
- **Avalonia MAUI backend** (Avalonia 12 preview) draws that homepage on Linux
  (`VardyParty.Desktop`)
- **C#** with nullable reference types
- **Auth0** for authentication (including QR-code device flow for TV)
- **System.Reactive** for reactive/observable patterns around stream updates
- Platform-specific native video players (not the UI toolkit):
  - **Android**: ExoPlayer with HLS support
  - **iOS / macOS**: AVPlayer
  - **Windows**: MediaPlayerElement
  - **Linux / WSL**: LibVLC (separate native video window — not Avalonia `VideoView`)

## Supported Platforms

- ✅ **Android** phones + **32-bit Android TV** (`armeabi-v7a` + `arm64-v8a`)
- ✅ **iOS**
- ✅ **macOS** (Mac Catalyst)
- ✅ **Windows** 10/11
- ✅ **Linux** (x64 and ARM64), including **WSL**, via `VardyParty.Desktop`

## Key Features

- **Live fixtures** -- Aggregates today's matches by league, enriches with BBC status/scores, and refreshes while you browse.
- **Stream Resolution & Health Checking** -- Discovers streams from multiple sources, tests their health (manifest availability, segment loading) in parallel, and prioritises healthy ones.
- **Automatic Stream Switching** -- Monitors playback health and seamlessly switches to backup streams on failure, maintaining a pool of healthy streams.
- **Native Video Playback** -- Platform-specific HLS/M3U8 players with custom header support; Linux video stays outside the Avalonia-drawn UI tree.
- **Android TV** -- 32-bit ARM field target, D-pad focus/scroll ownership, remote navigation, QR-code login, smooth shared homepage chrome.
- **Auth0 Authentication** -- Interactive login on mobile/desktop, device flow with QR code for TV.

## Project Structure

```
VardyParty/                  # Main MAUI application (Android/iOS/macOS/Windows)
├── HomeHostPage.xaml        # Hosts the shared XAML homepage + auth/resolve overlays
├── Platforms/               # Platform-specific implementations
│   ├── Android/             # Android services (video player, TV detection)
│   ├── iOS/                 # iOS video player
│   ├── MacCatalyst/         # macOS video player
│   └── Windows/             # Windows video player & overlay controls
├── Services/                # App-level services
└── Resources/               # Images, fonts, sounds, splash screens

VardyParty.HomeUi/           # Shared MAUI XAML homepage (rows, cards, brand logo)

VardyParty.Desktop/          # Linux desktop head (MAUI drawn by Avalonia)
├── Pages/                   # DesktopHomePage (device-code QR sign-in, playback)
└── Services/                # Auth0 device flow, LibVLC playback, UI sounds

VardyParty.Kernel/           # Shared models + config POCOs
VardyParty.Ports/            # Playback ports (launcher, switching, candidate rules)
VardyParty.Auth/             # Identity (token session, Auth0 HTTP, handler)
VardyParty.Catalog/          # Matcher, BBC, league filter, ticker, home presentation
VardyParty.Streaming/        # Orchestrator, resolvers, LAN, stream/M3U8 HTTP, health
VardyParty.Playback/         # Playback policy/session, switching pool, player ports
VardyParty.Presentation/     # Shared HomeShell/Menu view-models
VardyParty.Hosting/          # AddVardyParty() composition
```

## Architecture

- Service-oriented architecture with dependency injection
- Reactive programming (`IObservable` / `IObserver`) for tournament updates and progress
- Platform abstraction via `INativeVideoPlayerService`
- Orchestrator pattern for stream resolution workflow
- One homepage XAML (`HomeUi`); Linux paints it through the Avalonia MAUI backend; each OS keeps its own native player
