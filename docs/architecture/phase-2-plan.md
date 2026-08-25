# Phase 2 plan — host leftovers first, one frontend last

Phase 1 domain split, HTTP DualStack, and per-domain tests are in. Phase 2 finishes the leftover **host adapters**, then does **all UI in one phase**.

Do **not** thin `Home.razor` as its own slice. A Razor-only rewrite would be deleted when XAML lands. Shared VMs exist; remaining Home policy moves into those VMs **in the UI phase**, as the binding target for Android XAML — not as a Blazor cleanup.

**Test rules (locked)** for every new `[Fact]` / `[Theory]`: `// Arrange` / `// Act` / `// Assert`; AutoFixture specimens; `_fixture.GetMock<T>()` never `new Mock<T>()`; fictional names only; test projects do **not** enable ImplicitUsings; no Python. New tests go in the matching `tests/VardyParty.*.Tests` project.

Do not estimate calendar time. Each slice is a shippable PR.

---

## Why this exists

After PR #68 the HTTP / Home / C12 playback bar is met. What is **not** met:

| Leftover | UI? | Why it still matters |
|---|---|---|
| `LinuxAuthService` still owns PKCE + AES-GCM as one novel | No | Device-code/refresh already live in `Auth0TokenSession`. Linux should be storage + loopback browser. |
| No shared `PlaybackCommand` interpreter | No | Android/Windows copy effect execution; Linux skips flags. That is phase 1e. |
| `GetEnrichedStreamsAsync` has no callers | No | Phase 1e hygiene. |
| `Home.razor` still owns Home policy | Yes | XAML must bind the same VMs. Extract once, into XAML — not into thinner Razor. |
| `VideoPlayer.razor` `/player/...` | Yes | Confirm unused on the Home click path; retire or route through the orchestrator. |
| Blazor home on Android | Yes | C12 still pays Chromium for the card grid. Player chrome is already native. |

Happy Eyeballs stays. Do not drop DualStack without a C12 retest.

---

## Slice 1 — Linux auth is a host adapter

`Auth0TokenSession` already owns apply/clear/load/refresh + the session lock + JWT role. Linux interactive login uses the same session methods MAUI uses after the browser returns.

**Linux keeps:** AES-GCM token files, `xdg-open` / `HttpListener` loopback.  
**Auth keeps:** PKCE URL / S256 / loopback URI rules, authorization-code exchange, role gate, apply/clear.

**Do not** move OS storage into Auth.  
**Do not** merge Linux PKCE into Auth0.OidcClient.

**Tests first** in `VardyParty.Auth.Tests`: PKCE start/complete, persist-only-with-role, lock not held across the browser wait (same facts as MAUI interactive).

---

## Slice 2 — One `PlaybackCommand` interpreter (phase 1e)

`PlaybackCommandExecutor` in Playback. Android `NativeVideoActivity` and Windows `PlayerSession` call it with **identical** flag coverage. Linux grows to the same flags **after** characterization tests exist (do not “fix Linux” by copying untested flags).

Delete `GetEnrichedStreamsAsync` when tests prove no callers.

iOS/MacCatalyst only after the paid hosts share the interpreter.

**Tests first** in `VardyParty.Playback.Tests`: every `PlaybackCommand` flag the hosts currently interpret, plus Linux characterization before parity.

---

## Slice 3 — Frontend (one phase, last)

Follow [phase-2-webview-xaml.md](phase-2-webview-xaml.md). This is the **only** Razor / Blazor / XAML slice.

In the same PR sequence (tests first, then binding):

1. Move remaining Home policy out of `Home.razor` / `Home.Focus.cs` into `HomeShellViewModel` / `HomePlaybackIntent` / `MenuViewModel` (auth gate, flatten+filter, no auto-play `games[0]`, LAN warning, league flyout, overlay **commands**).
2. Android `HomePage.xaml` bound to those VMs (`#if ANDROID` or feature flag). Windows keeps Blazor as a second binding of the **same** VMs — no Razor-only rewrite.
3. Confirm `VideoPlayer.razor` `/player/...` is unused on the Home click path; retire or route through the orchestrator.
4. `NativeVideoActivity` unchanged. Ticker animation stays in the host.
5. Device-test C12 + Bravia: Home snappy, stream play, D-pad/focus.

**Do not** add bUnit. **Do not** migrate player chrome.  
Optional after this slice: Windows XAML home, then delete WebView, `wwwroot`, Razor, Cast JS.

---

## What we will not do in these slices

- Prefer IPv4, or drop Happy Eyeballs, without a C12 AAAA black-hole retest.
- A dedicated “make `Home.razor` thin” PR.
- `VardyParty.Domain` + `Application` + `Infrastructure` projects at solution level.
- Register `PlayerSession` / `NativeVideoActivity` as a DI session service.
- Merge `INativeVideoPlayerService` and `IMediaEngine`.
- Promote the Cast stub.
- Production worker deploys; Python tests; committing local `appsettings.json`.

---

## Suggested PR sequence

1. LinuxAuthService as storage + PKCE loopback adapter
2. `PlaybackCommandExecutor`; Linux parity as a follow-up; delete dead `GetEnrichedStreamsAsync`
3. Frontend: VMs + Android XAML + `VideoPlayer.razor` (one phase, last)
