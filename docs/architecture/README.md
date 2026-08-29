# Client architecture

Living architecture docs for VardyParty Client.

| Document | Role |
|----------|------|
| **[../ARCHITECTURE.md](../ARCHITECTURE.md)** | Heads, domain assemblies, pick→play diagrams |
| **[homepage-maui-avalonia.md](homepage-maui-avalonia.md)** | One MAUI XAML homepage everywhere; Avalonia draws it on Linux (`VardyParty.HomeUi` + `VardyParty.Desktop`). TV focus, clip chain, Crest spin, CI notes. |
| **[../STREAM_PLAYBACK_RULES.md](../STREAM_PLAYBACK_RULES.md)** | `PlaybackSessionController` / `IMediaEngine` |

Phase-plan canvases (phase-1/2 Blazor→XAML) were deleted after the work shipped;
history lives in git. Do not resurrect them as current design.

**Assemblies:** `Kernel`, `Ports`, `Auth`, `Catalog`, `Streaming`, `Playback`,
`Presentation` (shared VMs / pure policy), `Hosting` (`AddVardyParty`),
`HomeUi` (shared XAML), heads `VardyParty` and `VardyParty.Desktop`.
