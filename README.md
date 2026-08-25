# VardyParty

A cross-platform chess tournament streaming application built with .NET MAUI and Blazor. VardyParty aggregates live chess tournament streams, tests them for health and availability, and plays them using native video players with automatic stream switching if a feed goes down.

## Tech Stack

- **.NET MAUI** (.NET 10) with **Blazor WebView** for the UI (Razor components)
- **Avalonia UI** (for Linux support)
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
- ✅ **Linux** (x64 and ARM64) - See [Linux Support Guide](docs/LINUX_SUPPORT.md)

## Key Features

- **Tournament Discovery** -- Fetches chess tournament schedules, enriches them with pairings, live status, and player profiles. Real-time updates via background polling.
- **Stream Resolution & Health Checking** -- Discovers streams from multiple sources, tests their health (manifest availability, segment loading) in parallel, and prioritises healthy ones.
- **Automatic Stream Switching** -- Monitors playback health and seamlessly switches to backup streams on failure, maintaining a pool of healthy streams.
- **Native Video Playback** -- Platform-specific HLS/M3U8 players with custom header support.
- **Android TV Support** -- TV device detection, remote control navigation, and QR-code login flow.
- **Auth0 Authentication** -- Interactive login on mobile/desktop, device flow with QR code for TV.

## Project Structure

```
VardyParty/                  # Main MAUI application
├── Components/              # Blazor Razor components (Home, VideoPlayer, etc.)
├── Platforms/               # Platform-specific implementations
│   ├── Android/             # Android services (video player, TV detection)
│   ├── iOS/                 # iOS video player
│   ├── MacCatalyst/         # macOS video player
│   └── Windows/             # Windows video player & overlay controls
├── Services/                # App-level services
├── Resources/               # Images, fonts, splash screens
└── wwwroot/                 # Static web assets

VardyParty.Linux/            # Linux native application (Avalonia UI)
├── Services/                # Linux video player (LibVLC)
├── Assets/                  # Linux-specific icons
└── README.md                # Linux build & run instructions

VardyParty.Kernel/           # Shared models + config POCOs
VardyParty.Auth/             # Identity (token lifetime, Auth0 handler)
VardyParty.Catalog/          # Matcher, BBC, league filter, ticker, home presentation
VardyParty.Streaming/        # Orchestrator, resolvers, LAN, stream/M3U8 HTTP, health
VardyParty.Playback/         # Playback policy/session, switching pool, player ports
VardyParty.Presentation/     # Shared HomeShell/Menu view-models
VardyParty.Hosting/          # AddVardyPartyCore() composition
```

## Architecture

- Service-oriented architecture with dependency injection
- Reactive programming (`IObservable` / `IObserver`) for tournament updates and progress
- Platform abstraction via `INativeVideoPlayerService`
- Orchestrator pattern for stream resolution workflow

