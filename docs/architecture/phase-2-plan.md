# Phase 2 plan — Linux auth adapter + PlaybackCommand interpreter

Phase 1 domain split, HTTP DualStack, and per-domain tests are in. **Phase 2 is two non-UI slices.** Razor/Blazor/XAML home work is **Phase 3**, last — do not thin `Home.razor` here and do not start Android `HomePage.xaml`.

**Test rules (locked)** for every new `[Fact]` / `[Theory]`: `// Arrange` / `// Act` / `// Assert`; AutoFixture specimens; `_fixture.GetMock<T>()` never `new Mock<T>()`; fictional names only; test projects do **not** enable ImplicitUsings; no Python. New tests go in the matching `tests/VardyParty.*.Tests` project.

Do not estimate calendar time. Each slice is a shippable PR.

---

## Why this exists

After phase 1 the HTTP / Home / C12 playback bar is met. What is **not** met (and is still in scope here):

| Leftover | Why it still matters |
|---|---|
| `LinuxAuthService` was a second OAuth novel | Device-code/refresh already live in `Auth0TokenSession`. Linux PKCE URL/S256/loopback/exchange belongs in Auth; AES-GCM files + `xdg-open`/`HttpListener` stay in the Linux host. |
| No shared `PlaybackCommand` interpreter | Android/Windows copied effect execution; Linux skipped ReportFailed / ReportDeclined / RaiseBuffering / RetryFreshResolve. |

`Home.razor` still owns too much of the shell. That is **Phase 3**, so the XAML home binds the same VMs without rewriting policy twice.

Happy Eyeballs stays. Do not drop DualStack without a C12 retest.

---

## Slice 1 — Linux auth is a host adapter

`Auth0TokenSession` owns apply/clear/load/refresh + the session lock + JWT role + PKCE complete (code exchange, role gate, persist). `Auth0Pkce` owns authorize URL, S256, loopback redirect rules, listener prefix, and callback validation.

`LinuxAuthService` should be: AES-GCM token files + loopback PKCE browser (`HttpListener` + `xdg-open`/`gio`), calling those Auth helpers.

**Do not** move OS storage into Auth.  
**Do not** merge Linux PKCE into Auth0.OidcClient.

**Tests first** in `VardyParty.Auth.Tests`: PKCE start/complete, persist-only-with-role, lock not held across the browser wait (same fact as MAUI interactive).

---

## Slice 2 — One `PlaybackCommand` interpreter

`PlaybackCommandExecutor` in Playback. Android `NativeVideoActivity` and Windows `PlayerSession` call it with **identical** flag coverage. Linux characterization tests exist in `VardyParty.Playback.Tests`; Linux then uses the same executor (including the flags it previously skipped).

`GetEnrichedStreamsAsync` has no callers — delete it. `VideoPlayer.razor` `/player/...` is **Phase 3**. iOS/MacCatalyst use `PlaybackSessionController`, `PlaybackCommandExecutor`, and `PlaybackPoolCommandActions` from **`VardyParty.Playback`** — they do not reference `VardyParty.Linux`. AVPlayer attach/UI stays in the Apple hosts. Device characterization of AVPlayer still cannot run in this environment.

**Tests first** in `VardyParty.Playback.Tests`: every `PlaybackCommand` flag the hosts interpret, including ReportFailed / ReportDeclined / RaiseBuffering / RetryFreshResolve.

---

## Phase 3 (not this PR) — Home policy + Android XAML home

Follow [phase-2-webview-xaml.md](phase-2-webview-xaml.md) as the UI canvas. One UI phase, last:

1. Remaining Home policy out of `Home.razor` into the existing VMs (`HomeShellViewModel` / `HomePlaybackIntent` / `MenuViewModel`).
2. Feature-flag or `#if ANDROID` `HomePage.xaml` bound to those **same** VMs. Windows stays Blazor.
3. `NativeVideoActivity` unchanged. Ticker animation stays in the host.
4. Device-test C12 + Bravia: Home snappy, stream play, D-pad/focus.

**Do not** add bUnit. **Do not** migrate player chrome. **Do not** start this in Phase 2.

---

## What we will not do in Phase 2

- Prefer IPv4, or drop Happy Eyeballs, without a C12 AAAA black-hole retest.
- `VardyParty.Domain` + `Application` + `Infrastructure` projects at solution level.
- Register `PlayerSession` / `NativeVideoActivity` as a DI session service.
- Merge `INativeVideoPlayerService` and `IMediaEngine`.
- Production worker deploys; Python tests; committing local `appsettings.json`.
- Thin Home.razor or start Android `HomePage.xaml`.

---

## Suggested PR sequence

1. LinuxAuthService as storage + PKCE adapter (Auth owns URL/S256/exchange/role)
2. `PlaybackCommandExecutor`; Android/Windows identical flags; Linux parity after characterization tests
3. **Phase 3:** Home.razor → VMs and Android `HomePage.xaml` in one UI phase
