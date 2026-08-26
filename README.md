# VardyParty

A cross-platform match streaming application built with .NET MAUI. VardyParty aggregates live streams, tests them for health and availability, and plays them using native video players with automatic stream switching if a feed goes down.

## Tech Stack

- **.NET MAUI** (.NET 11 preview) with a shared **MAUI XAML homepage** (`VardyParty.HomeUi`)
- **MAUI-Avalonia** (Avalonia 12 preview backend) draws the same homepage on Linux (`VardyParty.Desktop`) — see [docs/architecture/homepage-maui-avalonia.md](docs/architecture/homepage-maui-avalonia.md)
- **C#** with nullable reference types
- **Auth0** for authentication (including QR-code device flow for TV)
- **System.Reactive** for reactive/observable patterns around stream updates
- Platform-specific native video players:
  - **Android**: ExoPlayer with HLS support
  - **iOS / macOS**: AVPlayer
  - **Windows**: MediaPlayerElement
  - **Linux**: LibVLC

## Supported Platforms

- ✅ **Android** (including Android TV)
- ✅ **iOS**
- ✅ **macOS** (Mac Catalyst)
- ✅ **Windows** 10/11
- ✅ **Linux** (x64 and ARM64) via `VardyParty.Desktop` - See [docs/architecture/homepage-maui-avalonia.md](docs/architecture/homepage-maui-avalonia.md)

## Key Features

- **Tournament Discovery** -- Fetches chess tournament schedules, enriches them with pairings, live status, and player profiles. Real-time updates via background polling.
- **Stream Resolution & Health Checking** -- Discovers streams from multiple sources, tests their health (manifest availability, segment loading) in parallel, and prioritises healthy ones.
- **Automatic Stream Switching** -- Monitors playback health and seamlessly switches to backup streams on failure, maintaining a pool of healthy streams.
- **Native Video Playback** -- Platform-specific HLS/M3U8 players with custom header support.
- **Android TV Support** -- TV device detection, remote control navigation, and QR-code login flow.
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

