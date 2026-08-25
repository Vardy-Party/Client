# Client architecture

This folder is the **architecture canvas and plan** for moving VardyParty off a single `VardyParty.Core` grab-bag onto **domain assemblies**, with Clean Architecture rings **inside** each domain.

| | Document |
|---|---|
| **Canvas (phase 1)** | [phase-1-canvas.md](phase-1-canvas.md) — assemblies, layers, playback flow |
| **Plan (phase 1)** | [phase-1-plan.md](phase-1-plan.md) — how we get there, tests first |
| **Phase 2 plan** | [phase-2-plan.md](phase-2-plan.md) — Linux auth, PlaybackCommand, then one frontend phase (VMs + XAML) last |
| **Phase 2 UI canvas** | [phase-2-webview-xaml.md](phase-2-webview-xaml.md) — drop Blazor WebView for MAUI XAML (Android perf). **Last slice. Do not thin Razor first.** |

Existing [docs/ARCHITECTURE.md](../ARCHITECTURE.md) is **version/CI**, not this app. Playback as-is: [docs/STREAM_PLAYBACK_RULES.md](../STREAM_PLAYBACK_RULES.md).

**Phase 1 assemblies:** `VardyParty.Kernel`, `VardyParty.Ports`, `VardyParty.Auth`, `VardyParty.Catalog`, `VardyParty.Streaming`, `VardyParty.Playback`, `VardyParty.Presentation` (shared VMs), and `VardyParty.Hosting` (`AddVardyParty`). `VardyParty.Core` is deleted. Blazor WebView stays until [phase 2](phase-2-webview-xaml.md).
