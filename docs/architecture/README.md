# Client architecture

This folder is the **architecture canvas and plan** for moving VardyParty off a single `VardyParty.Core` grab-bag onto **domain assemblies**, with Clean Architecture rings **inside** each domain.

| | Document |
|---|---|
| **Canvas (phase 1)** | [phase-1-canvas.md](phase-1-canvas.md) — assemblies, layers, playback flow |
| **Plan (phase 1)** | [phase-1-plan.md](phase-1-plan.md) — how we get there, tests first |
| **Phase 2 (separate)** | [phase-2-webview-xaml.md](phase-2-webview-xaml.md) — drop Blazor WebView for MAUI XAML (Android perf). **Not in phase 1.** |

Existing [docs/ARCHITECTURE.md](../ARCHITECTURE.md) is **version/CI**, not this app. Playback as-is: [docs/STREAM_PLAYBACK_RULES.md](../STREAM_PLAYBACK_RULES.md).

**In progress:** extracted `VardyParty.Kernel`, `VardyParty.Playback`, `VardyParty.Auth`, `VardyParty.Presentation` (shared VMs), and `VardyParty.Hosting` (`AddVardyPartyCore`). Catalog and Streaming still live in `VardyParty.Core`.
