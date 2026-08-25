# Phase 2 plan — remaining review items, then XAML home

Phase 1 domain split, HTTP DualStack, and per-domain tests are in. This is the ordered list of **fixes the last PR review still called out**, then the WebView→XAML home that [phase-2-webview-xaml.md](phase-2-webview-xaml.md) already canvases.

Do **not** start Android `HomePage.xaml` until slice 1 is done. Shared VMs exist; `Home.razor` still owns too much of the shell.

**Test rules (locked)** for every new `[Fact]` / `[Theory]`: `// Arrange` / `// Act` / `// Assert`; AutoFixture specimens; `_fixture.GetMock<T>()` never `new Mock<T>()`; fictional names only; test projects do **not** enable ImplicitUsings; no Python. New tests go in the matching `tests/VardyParty.*.Tests` project.

Do not estimate calendar time. Each slice is a shippable PR.

---

## Why this exists

After `d441de7` the HTTP / Home / C12 playback bar is met. What is **not** met:

| Leftover | Why it still matters |
|---|---|
| `Home.razor` ~968 lines | Phase 2 XAML must bind the **same** VMs. Logic still in Razor will be rewritten twice. |
| `LinuxAuthService` ~465 lines | Device-code/refresh already live in `Auth0TokenSession`. Linux PKCE/storage is still a second OAuth novel. |
| No shared `PlaybackCommand` interpreter | Android/Windows copy effect execution; Linux skips flags. That is phase 1e, still open. |
| Blazor home on Android | C12 still pays Chromium for the card grid. Player chrome is already native. |

Happy Eyeballs stays. Do not drop DualStack without a C12 retest.

---

## Slice 1 — Home.razor owns layout, not policy (still Blazor)

**In:** `HomeShellViewModel` / `HomePlaybackIntent` / `MenuViewModel`.  
**Out:** Android `HomePage.xaml` (slice 4).

Move out of `Home.razor` (and `Home.Focus.cs`) into the existing VMs or tiny presentation helpers:

- Auth gate + QR / device-login continue-cancel focus (already partly in VMs).
- Flatten + filter + “no auto-play `games[0]`” (intent already exists — stop duplicating in Razor).
- LAN warning, league flyout, stream-discovery overlay **commands** (not the markup).
- Keep Razor as markup + `@bind` / event forwarders only.

**Tests first** in `VardyParty.Presentation.Tests`: every command Home currently inlines. `Home.razor` line count should drop because behavior moved, not because markup was deleted.

Windows stays Blazor. Linux `MainWindowViewModel` already consumes the shared VMs — do not fork a third copy.

---

## Slice 2 — Linux auth is a host adapter

`Auth0TokenSession` already owns apply/clear/load/refresh + the session lock + JWT role. `LinuxAuthService` should be: AES-GCM token files + loopback PKCE browser, calling the same session methods MAUI uses.

**Do not** move OS storage into Auth (canvas: hosts keep storage).  
**Do not** merge Linux PKCE into Auth0.OidcClient.

**Tests first** in `VardyParty.Auth.Tests`: PKCE start/complete, persist-only-with-role, lock not held across the browser wait (same facts as MAUI interactive).

---

## Slice 3 — One `PlaybackCommand` interpreter (phase 1e)

`PlaybackCommandExecutor` in Playback. Android `NativeVideoActivity` and Windows `PlayerSession` call it with **identical** flag coverage. Linux grows to the same flags **after** characterization tests exist (do not “fix Linux” by copying untested flags).

Then: confirm `VideoPlayer.razor` `/player/...` is unused on the Home click path; retire or route through the orchestrator. iOS/MacCatalyst only after the paid hosts share the interpreter.

**Tests first** in `VardyParty.Playback.Tests`: every `PlaybackCommand` flag the hosts currently interpret.

---

## Slice 4 — Android XAML home (the original phase 2 canvas)

Follow [phase-2-webview-xaml.md](phase-2-webview-xaml.md). Only after slice 1.

1. Feature-flag or `#if ANDROID` `HomePage.xaml` bound to the **same** `HomeShellViewModel` / `MenuViewModel`. Windows stays Blazor.
2. `NativeVideoActivity` unchanged. Ticker animation stays in the host.
3. Device-test C12 + Bravia: Home snappy, stream play, D-pad/focus.
4. Optional later: Windows XAML home, then delete WebView, `wwwroot`, Razor, Cast JS.

**Do not** add bUnit. **Do not** migrate player chrome.

---

## What we will not do in these slices

- Prefer IPv4, or drop Happy Eyeballs, without a C12 AAAA black-hole retest.
- `VardyParty.Domain` + `Application` + `Infrastructure` projects at solution level.
- Register `PlayerSession` / `NativeVideoActivity` as a DI session service.
- Merge `INativeVideoPlayerService` and `IMediaEngine`.
- Production worker deploys; Python tests; committing local `appsettings.json`.

---

## Suggested PR sequence

1. Home.razor → VMs (Blazor markup only) — **blocks XAML**
2. LinuxAuthService as storage + PKCE adapter
3. `PlaybackCommandExecutor`; Linux parity as a follow-up
4. Android `HomePage.xaml` + C12/Bravia device pass
