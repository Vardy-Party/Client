# The homepage, rewritten once: MAUI XAML drawn by Avalonia

This document explains the new homepage stack introduced by the
`VardyParty.HomeUi` + `VardyParty.Desktop` projects, and answers the
architecture questions that motivated it. It supersedes the approach sketched
in [phase-2-webview-xaml.md](phase-2-webview-xaml.md) (which assumed the
MAUI XAML rewrite could not cover Linux).

## The questions, answered plainly

### How does MAUI fit with XAML?

.NET MAUI is a UI framework whose XAML maps to **native controls**: a MAUI
`Button` becomes an Android `MaterialButton`, a WinUI `Button` on Windows, a
`UIButton` on iOS. That is fast because the platform draws its own widgets.

The VardyParty app used to **bypass all of that**: `MainPage.xaml` hosted a
single `BlazorWebView`, and the whole homepage was HTML/CSS running in a
WebView (`Components/Pages/Home.razor`, ~968 lines). On a 32-bit Android TV
box that meant an embedded browser engine doing layout, style and JS on
hardware that struggled with it — which was exactly the sluggishness we saw.
Replacing the WebView with real MAUI XAML (`CollectionView` and friends) is
the performance fix: virtualized native views instead of DOM. **On this
branch that replacement is complete and the Blazor UI is deleted** —
`Components/`, `wwwroot/`, `MainPage.xaml`, the
`Microsoft.AspNetCore.Components.WebView.Maui` package, the Cast JS interop
and the scoped-CSS build plumbing are all gone; every platform (Android,
Windows, iOS, Mac Catalyst, Linux desktop) boots the shared XAML homepage.

### How does .NET on Linux fit with XAML?

It doesn't, out of the box. There is **no Microsoft UI stack for Linux**:
WPF and WinUI are Windows-only, and MAUI has never shipped a Linux target.
.NET itself runs fine on Linux — what was missing is the UI layer.

**Avalonia** fills that gap: it is a XAML framework that does not use native
controls at all. It draws every pixel itself with Skia (the same graphics
library Chrome and Flutter use), so the same UI runs anywhere Skia runs —
including Linux. The retired `VardyParty.Linux` app was Avalonia 11 with its
own hand-written window; its XAML dialect was *similar* to MAUI's but not
compatible, which is why the app previously needed two homepage
implementations. That head is now deleted; `VardyParty.Desktop` (MAUI drawn
by Avalonia) is the only Linux head.

### What changed: the MAUI-Avalonia backend

[MAUI Avalonia Preview 1](https://avaloniaui.net/blog/maui-avalonia-preview-1)
(Avalonia 12 / .NET 11 previews) adds an **Avalonia backend for .NET MAUI**:
instead of mapping MAUI controls to native widgets, it maps them to
Avalonia-drawn controls. Add the `Avalonia.Controls.Maui.Desktop` package to
a `net11.0` MAUI app, call `UseAvaloniaApp()` on the `MauiAppBuilder`, and a
source generator emits the desktop `Program.Main` bootstrap.

The consequence for us: **one MAUI XAML homepage** can now serve every
platform —

| Platform | Renderer |
|---|---|
| Android TV / phone | MAUI → native Android views (fast; no WebView) |
| Windows | MAUI → WinUI |
| iOS / Mac Catalyst | MAUI → UIKit |
| **Linux desktop** | MAUI → **Avalonia-drawn** (`VardyParty.Desktop`) |
| WASM (future) | MAUI → Avalonia-drawn in the browser |

## What this PR ships

```
VardyParty.HomeUi/            shared MAUI XAML homepage (net11.0 + net11.0-android/-windows)
  Views/HomeView.xaml         Netflix-style rows + brand header + league/settings menu
  Views/MatchCardView.xaml    rich match card (badges, score, status, effects)
  Views/BrandLogoView.xaml    3D metallic animated Vardy Party crest (see below)
  ViewModels/                 HomeViewModel, LeagueRowViewModel, MatchCardViewModel,
                              LeagueToggleViewModel, HomeLayoutState
  Services/                   IBadgeImageLoader (+ Svg.Skia rasterizer), IHomeAssetLocator,
                              BrandCrestImageLoader
  Resources/brand_crest.svg   metallic crest, embedded + rasterised at runtime

VardyParty/                   MAUI head (net11.0-android/-ios/-maccatalyst/-windows)
  HomeHostPage.xaml           hosts HomeView + auth/resolve overlays on every platform
                              (the Blazor UI is deleted)

VardyParty.Desktop/           Linux/desktop head (net11.0, UseAvaloniaApp)
  MauiProgram.cs              AddVardyParty + AddVardyPartyHttpClients + HomeUi DI
  Pages/DesktopHomePage.xaml  hosts HomeView + device-code QR sign-in + playback overlays
  Services/DesktopAuthService.cs        Auth0 PKCE loopback / device-code flow (from VardyParty.Linux)
  Services/DesktopVideoPlayerService.cs LibVLC playback in a native video window (see below)
  Services/SoundFlowUiSoundPlayer.cs    UI sounds (miniaudio); degrades gracefully headless
  Services/SampleGames.cs     VARDYPARTY_DESKTOP_SAMPLE_DATA=1 offline data

VardyParty.Presentation/Application/Home/
  TeamPalette.cs              curated club colours + deterministic HSL fallback
  MatchStatusPresenter.cs     phase/chip/score/aggregate/kick-off formatting
  HomeRowsBuilder.cs          league-row grouping and ordering (live rows first)
  HomeLayoutClass.cs/.Metrics HomeLayoutClassifier: TV/Desktop/PhoneLandscape/Portrait
```

Deleted on this branch (no dormant rollback code):

- `VardyParty/Components/` (all Razor pages/layout/routes), `VardyParty/wwwroot/`,
  `MainPage.xaml` + `StubBlazorWebViewHandler` + Cast JS interop +
  `BuildInfoService`/`CastService`, the `Microsoft.AspNetCore.Components.WebView.Maui`
  package, the Razor project SDK and every scoped-CSS/wwwroot sync step in
  the csproj and `scripts/run-windows-debug.ps1`.
- `VardyParty.Linux/` (the Avalonia 11 head with the ListBox homepage). Its
  two unique capabilities were ported into `VardyParty.Desktop` first: the
  Auth0 device-code sign-in with QR (`DesktopAuthService` + QRCoder) and
  LibVLC playback (`DesktopVideoPlayerService`). `VardyParty.slnx`, CI, CD
  snap packaging and `scripts/launch-linux-app.cmd` now point at the
  Desktop head.

The pure logic lives in `VardyParty.Presentation` (net11.0, fully
unit-tested in `tests/VardyParty.Presentation.Tests`) so the existing MAUI
app can adopt it without touching the preview stack.

### Everything is net11.0 now

Every project in the repo — the domain libraries (Kernel, Ports, Catalog,
Auth, Streaming, Playback, Presentation, Hosting), the test projects
(`tests/Directory.Build.props`) and `Tools/StreamHealthCheckerTool` — targets
net11.0, matching the MAUI/Desktop heads. The
`Microsoft.Extensions.Logging.Abstractions` / `Options` /
`Configuration.Abstractions` packages are framework-provided on .NET 11 and
their explicit `PackageReference`s were removed (they fired NU1510; the
packaging flows are zero-warning again). CI/CD pins a single
`DOTNET_VERSION: "11.0.x"` with `dotnet-quality: preview` for every job —
`DOTNET_PREVIEW_VERSION` is gone.

### The Netflix-style UI in XAML terms

- **Rows**: a vertical `CollectionView` of league rows; each row hosts a
  horizontal `CollectionView` of match cards. Both virtualize, which is the
  point on 32-bit Android TV hardware.
- **Match cards**: home/away names, in-game score, aggregate ("Agg 1-1"),
  kick-off time ("3:00 PM" / "Tomorrow 12:30 PM" / "Sep 02, 8:00 PM"),
  playing minutes with stoppage ("45+2'"), HT/FT/extra time/penalties/
  postponed chips — all from `MatchStatusPresenter`.
- **Ephemeral team-colour graphics**: each card gets a diagonal
  `LinearGradientBrush` wash from `TeamPalette` (home colour top-left, away
  bottom-right) plus team-colour accent edges.
- **Treated badges**: remote BBC SVG badges are rasterized with `Svg.Skia`
  and wrapped in a metallic gradient ring with a gloss highlight and drop
  shadow; teams without a badge get a monogram disc in their team colour.
- **Animation, kept cheap**: pulsing live dot (opacity/scale), card scale on
  hover/focus, a sheen sweep on pointer-over — transforms and opacity only,
  no per-frame layout.
- **League menu**: overlay bound to the existing `MenuViewModel`/
  `ILeagueFilterService` — checkbox per league, Show all, Reset to defaults.

### Adaptive layout

`HomeLayoutClassifier` picks one of **TV / Desktop / PhoneLandscape /
PhonePortrait** from window size + television idiom, and
`HomeLayoutMetrics` supplies concrete sizes (card size, badge size, brand
logo size, font sizes, paddings) which the XAML binds. TV gets 10-foot
sizing (360×190 cards after a field report that 440×232 was oversized —
~4 cards per row and ~3 league rows now fit a 1080p panel); phones get
smaller cards and tighter padding, portrait tighter still.

### TV focus (Android leanback, D-pad)

MAUI's `Focused`/`VisualStateManager` and Android's native view focus are
**separate systems**: on a leanback box the D-pad drives *native* focus, and
a MAUI `Border` is not natively focusable, so without extra wiring the D-pad
would skip the cards entirely and MAUI focus events would never fire.
`MatchCardView` therefore bridges natively (Android + TV idiom only):

- The card root's platform view is made `Focusable` (not
  `FocusableInTouchMode`), with `DescendantFocusability=BlockDescendants` so
  focus search always lands on the card root, never a child.
- A native `FocusChange` listener drives the **same** highlight chrome as
  the MAUI `Focused` path (scale 1.09 + bright `#AFCBFF` 3 px border + glow
  shadow) and the focus-tick sound (`UiSoundService.FocusMove` via
  `MatchCardViewModel.FocusMoved`), so whichever focus system fires, the
  card lights up and ticks. The MAUI-side `Focused`/pointer handlers remain
  for Windows/Desktop.
- A native `Click` listener fires the pick — a focused clickable Android
  view converts DPAD_CENTER/Enter into a click itself.
- The wiring follows the platform view (`HandlerChanged` + `Loaded` +
  `BindingContextChanged`), so handler-timing and RecyclerView recycling
  can never leave a card unfocusable, and it is torn down on `Unloaded`.
- On focus gained the card calls `ScrollTo(..., MakeVisible)` on both its
  horizontal strip and the vertical rows list: native RecyclerView focus
  scrolling reveals a card only partially, which clips the focus glow.
- One-shot autofocus: `HomeViewModel` arms `RequestsInitialFocus` on the
  first card of the first row on the empty→non-empty edge; the view consumes
  it once and calls `RequestFocus()` on the native view, so the app opens
  with a visibly focused card. Later refreshes never steal the highlight.

Key routing: `RemoteKeyHandler` (activity level) deliberately has **no
D-pad direction cases** — it only consumes media keys, Menu, Back and
(conditionally) Enter. Note that `Activity.OnKeyDown` logging every
`DpadUp/Down/...` press is *expected even when traversal works*: the
activity sees a key when the focused view declines it, and `ViewRootImpl`
performs D-pad focus navigation only after the whole dispatch chain
declines. Activity-level D-pad logs are therefore not evidence that focus
is broken.

### The brand logo (3D, metallic, animated)

The header is a brand row: the Vardy Party crest left of the wordmark with
the subtitle beneath, on every adaptive layout
(`HomeLayoutMetrics.BrandLogoSize`: TV 68 / desktop 58 / phone 46–40 dip).

- **Asset**: `VardyParty.HomeUi/Resources/brand_crest.svg` re-authors the
  app-icon soccer-ball geometry (`Resources/AppIcon/appiconfg.svg`) with
  chrome/navy metallic gradients, and is rasterised once per process through
  the same Svg.Skia path the badges use (`BrandCrestImageLoader`).
- **3D treatment** (`BrandLogoView`): the badges' brushed-metal gradient
  ring, a dark inner plate, a glass gloss over the upper hemisphere, and a
  drop shadow.
- **Animation**: a sheen sweep on load, a slow ambient shimmer loop (a
  low-opacity sheen crosses the crest for a quarter of a 6 s loop), and a
  subtle scale + glow + sheen response when TV focus enters the header
  (the Menu button). Same performance discipline as the cards —
  opacity/transform only, everything aborted on unload.

### UI sound design

Six generated WAV cues (`VardyParty/Resources/Raw/Sounds`) played through
`UiSoundService` (`VardyParty.Presentation`): navigation blip on TV focus
moves (rate-limited), select confirmation, stream-ready, error, goal chime
(via `ScoreChangeDetector`) and app-open sting. Platform players:
`SoundPool` on Android, `MediaPlayer` on Windows, SoundFlow (miniaudio) on
the Desktop head — which disables itself cleanly when no audio device
exists (headless CI). Sounds are suppressed while the native video player
is visible (`INativeVideoPlayerService.PlaybackVisibilityChanged`) and can
be turned off in the menu's Settings section (persisted per platform).

## Exact stack versions (verified building and running on Linux)

| Component | Version |
|---|---|
| .NET SDK | `11.0.100-preview.7.26381.103` (channel 11.0, quality preview) |
| `Microsoft.Maui.Controls` | `11.0.0-preview.7.26406.9` (nuget.org) |
| `Avalonia.Controls.Maui.Desktop` | `11.0.0-preview.7.26224.328` (nuget.org; Avalonia 12 preview underneath) |
| MAUI workload on Linux | `maui-tizen` (the only workload that carries the plain-TFM MAUI SDK on Linux) |
| `Svg.Skia` | `5.2.2` |
| `SkiaSharp.NativeAssets.Linux` | `4.148.0` (explicit pin, see gotchas) |

### Repo-specific gotchas (hard-won, do not rediscover)

1. **`Directory.Build.targets` vs multi-targeting**: the repo strips
   `TargetFramework` off every `ProjectReference` (to protect net11.0 domain
   libraries from Android TFM/RID leakage). That also strips the
   `SetTargetFramework` negotiation used by *cross-targeting* references, so
   a project declaring plural `<TargetFrameworks>` returns **no compiled
   output** to its referencing project (CS0234/CS0246 despite a successful
   reference build). `VardyParty.HomeUi` and `VardyParty.Desktop` therefore
   declare singular `<TargetFramework>`. When HomeUi later multi-targets
   (`net11.0;net11.0-android`), referencing heads must override
   `GlobalPropertiesToRemove` on that `ProjectReference`.
2. **SkiaSharp native mismatch**: `Svg.Skia` pins
   `SkiaSharp.NativeAssets.Linux` 3.119 while Avalonia 12 preview's managed
   SkiaSharp is 4.148. Without the explicit 4.148 pin the app aborts at
   startup with "native libSkiaSharp (119.0) incompatible".
3. **LibVLC in a MAUI-Avalonia window**: `LibVLCSharp.Avalonia`'s
   `VideoView` is an Avalonia control with no MAUI handler, so it cannot be
   hosted inside the Desktop head's MAUI XAML tree (and the Avalonia-12
   preview backend exposes no supported native-surface embedding hook).
   `DesktopVideoPlayerService` therefore uses plain `LibVLCSharp` and lets
   libvlc open its own native video window; the in-app "Now Playing"
   overlay owns the Close control. libvlc is initialised lazily on first
   play so machines without VLC still run the homepage.

## CI shape

The `ci.yml` pipeline is ordered so **Code Quality gates every platform
build**:

1. `test` (SDK 11 preview, all `tests/*Tests` projects), then
2. `code-quality` (SDK 11 preview: analyzers with warnings-as-errors +
   `dotnet format --verify-no-changes`), then
3. `build-android`, `build-windows`, `build-ios`, `build-macos` and
   `build-desktop` — each with `needs: code-quality`, and finally
4. `desktop-runtime-smoke` (needs `build-desktop`): rebuilds the Desktop
   head and runs it for 20 s under `xvfb-run` with
   `VARDYPARTY_DESKTOP_SAMPLE_DATA=1` — the startup smoke test that used to
   guard `VardyParty.Linux`.

`ci-complete` fails on any failure of test/code-quality/android/windows/
desktop-build/desktop-smoke (iOS/macOS remain informational, as before).
The old `build-linux` and `linux-runtime-smoke` jobs are gone with the
project; CD's Linux snap jobs package `VardyParty.Desktop` instead.

## Local packaging (PowerShell)

`package-android.cmd` / `package-windows.cmd` are now `package-android.ps1`
and `package-windows.ps1` (the `.cmd` files are deleted). The Android script
keeps the whole contract: the SDK-11 fail-fast guard, the appsettings
secrets-patching flow (`-p:PatchAppSettings=true` → the
`PatchAppSettingsForLocalAndroid` csproj target →
`scripts/patch-appsettings-android.ps1`), the domain restore dance (still
required on net11 — the MAUI restore rewrites the domain
`project.assets.json` and the next plain-net11.0 build hits NETSDK1005
without the re-restore), the `AndroidArmOnly` device default with
`-Mode all` for the store/emulator fat APK, build-info/splash generation and
the canonical multi-ABI APK check (`scripts/assert-android-apk-abis.ps1`).

New in the PowerShell version:

- `-m:1` on the trim-heavy Release build — parallel multi-RID ILLink crashed
  local packaging from memory pressure; the failure hint reminds that Windows
  needs a page file for trimming.
- XA5207 detection that prints the `InstallAndroidDependencies` remedy
  (run elevated if the Android SDK lives under Program Files).
- The secrets-patched `VardyParty/appsettings.json` is `git restore`d after
  the build (clearly logged; opt out with `-KeepPatchedAppSettings`).

`buildinfo.txt` is no longer a tracked file the build rewrites: the
`GenerateBuildInfo` target writes `$(IntermediateOutputPath)buildinfo.txt`
and injects it as a `MauiAsset` with the same `buildinfo.txt` logical name,
so packages still ship it (`assets/buildinfo.txt` in the APK) but the
working tree stays clean after every build.

## Startup hardening (real-device crash fixes)

Two startup crashes found on real devices after the net11/CoreCLR move:

- **Android TV (32-bit, CoreCLR)**: `AddSecrets`
  (`VardyParty.Hosting/Infrastructure/Exceptions/ServiceCollectionExtensions.cs`)
  probed `appsettings.json` with a relative-path JSON file source. On Mono
  the default configuration base path happened to be absolute (`/`) so the
  optional file just never loaded; on CoreCLR Android it is not absolute and
  `PhysicalFileProvider` threw "The path must be absolute" inside
  `ConfigurationBuilder.Build()` before the app could start. The probe now
  resolves an explicit absolute path from `AppContext.BaseDirectory`, only
  adds file sources that exist, and the whole optional secrets flow is
  guarded — a missing secrets source can never crash startup. Devices keep
  using the embedded `appsettings.json`; desktop user-secrets behavior is
  unchanged.
- **Windows (WinAppSDK 1.8)**: a stowed-exception crash (`0xc000027b` in
  `CoreMessagingXP.dll`) between window creation and first render. Every
  window-chrome hook (`MauiProgram` lifecycle events and mapper hooks,
  `WindowsWindowChrome`, `WindowsWindowDragHelper`) is now wrapped in
  try/catch that logs via `WindowsEventLogger` and degrades to default
  chrome; chrome no longer calls `AppWindow.Show()`/`Activate()` from inside
  content-connect mappers (MAUI shows its own window); the deprecated
  `AppWindowTitleBar.SetDragRectangles` is replaced by
  `InputNonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption)`
  with the old call as a logged fallback; Auth0's
  `CheckRedirectionActivation` only short-circuits startup for genuine
  protocol activations and treats activator failures as a normal launch.
  Setting `VARDYPARTY_NO_CHROME=1` skips all custom chrome for bisecting.

The full WER report pins the Windows stowed exception as `combase.dll`
HRESULT `0x800710DD` — the WinUI 3 DispatcherQueue/CoreMessaging misuse
signature (a WinRT operation on the wrong thread/apartment, or an
**unobserved async WinRT failure**, which a managed try/catch cannot catch).
The two live suspects are each behind their own startup kill switch for a
clean bisect on the affected machine:

- `VARDYPARTY_NO_CHROME=1` — skip all custom window chrome (Windows).
- `VARDYPARTY_NO_SOUND=1` — register `NullUiSoundPlayer` instead of the
  platform sound player on every platform (Windows `MediaPlayer`, Android
  `SoundPool`, Desktop SoundFlow); `VardyParty.Ports.UiSoundKillSwitch`,
  checked once at registration and logged.

Each switch has **two mechanisms**, and the startup log line says which one
triggered:

1. **Environment variable** (`VARDYPARTY_NO_CHROME=1` / `VARDYPARTY_NO_SOUND=1`).
   Beware on packaged Windows: MSIX apps launched via `shell:AppsFolder`
   (which is how `run-windows-debug.ps1` and the Start menu launch them) are
   activated by Explorer and **do not inherit the terminal's environment**.
   A terminal-scoped `$env:VARDYPARTY_NO_SOUND=1` therefore never reaches
   the app; `setx VARDYPARTY_NO_SOUND 1` (then sign out/in or restart
   Explorer so the user environment is re-read) does, at the cost of
   persisting machine-wide until you `setx` it back to empty.
2. **Flag file** (`VardyParty.Ports.StartupFlagFiles`): the mere presence of
   `%LOCALAPPDATA%\VardyParty\flags\no-chrome` or
   `%LOCALAPPDATA%\VardyParty\flags\no-sound` enables the switch — file
   contents are ignored; delete the file to re-enable. This reaches packaged
   apps regardless of how they are activated. On non-Windows platforms the
   same names are probed under the per-user `LocalApplicationData` and
   `ApplicationData` folders, in `VardyParty/flags/`. Example:

   ```powershell
   New-Item -ItemType File -Force "$env:LOCALAPPDATA\VardyParty\flags\no-sound"
   # ... reproduce / bisect ...
   Remove-Item "$env:LOCALAPPDATA\VardyParty\flags\no-sound"
   ```

`WindowsUiSoundPlayer` itself was audited against the 0x800710DD signature:
`Windows.Media.Playback.MediaPlayer` is WinRT-agile (metadata
MarshalingBehavior=Agile, ThreadingModel=Both), so background-thread
creation is legal — but async media failures are now observed via a
never-throwing `MediaFailed` logging handler, `AutoPlay=false` is explicit,
and `CommandManager.IsEnabled=false` keeps the sound-effect players out of
the System Media Transport Controls / CoreMessaging machinery entirely.

A follow-up investigation pinned the crash to ~1 second after the first
games update renders, and hardened the remaining path end to end:

- **XAML-thread exception hook** (`VardyParty/Platforms/Windows/App.xaml.cs`):
  the WinUI `Application.UnhandledException` event is the only hook that
  sees XAML-thread exceptions before WinAppSDK 1.8 converts them into
  anonymous stowed 0xc000027b crashes — neither
  `AppDomain.UnhandledException` nor `TaskScheduler.UnobservedTaskException`
  is ever raised for them. It is wired as the first statement of the WinUI
  `App` constructor (`Application.Current` is valid from the base ctor, so
  even `InitializeComponent` failures are covered), logs exception + stack
  via `WindowsEventLogger.Fatal`, and marks the exception handled so the
  app survives where possible.
- **Serialized + coalesced games publishes** (`EnrichedGameService`): the
  API and BBC pollers each ended in `RunMatching` → `_subject.OnNext` with
  no synchronization, so `GamesStream` could emit from two threads at once,
  and the two startup fetches published two full boards ~1s apart — a full
  board reset mid-first-materialization of the nested CollectionViews is
  WinUI's documented 0x800710DD failure mode, making this the leading crash
  trigger. A private publish lock now serializes the whole match+publish
  body, and the startup burst is coalesced: if the API fetch wins the race
  its standalone publish is skipped (at most once, ever) and the initial
  BBC completion — success or failure — publishes the one enriched board; a
  3s grace fallback publishes the API-only board if BBC hangs. Steady-state
  live-score publishes are never delayed (no debounce; the skip is spent
  after startup).
- **Single-threaded goal detector** (`HomeViewModel`): the
  `ScoreChangeDetector`'s plain `Dictionary` is now only touched inside the
  dispatched `Apply` (and `ResetScoreObservations` dispatches too), so it
  is single-threaded by construction rather than by the publisher's lock
  discipline.
- **No sync-over-async on the dispatcher** (`MauiHomeAssetLocator`):
  `ResolveLeagueLogoPath` blocked on `OpenAppPackageFileAsync` with
  `GetAwaiter().GetResult()` and is reached on the UI thread at first
  render; `IHomeAssetLocator.ResolveLeagueLogoPathAsync` is now genuinely
  async (both the MAUI and Desktop locators updated).
- **Sounds in the AppX layout**: `SyncSoundsToWindowsLayout` (csproj)
  mirrors the league-logos target so `Resources\Raw\Sounds\**` lands in
  both the win-x64 output root and `AppX\Sounds\` on VS/dotnet-driven
  incremental syncs; `run-windows-debug.ps1`'s recursive sync already
  covered script-driven deploys.

## Follow-ups

- **iOS / Mac Catalyst runtime QA**: both platforms boot `HomeHostPage`
  and CI builds them, but nobody has run the new UI on real Apple hardware
  yet.
- **Windows drag region**: header drag now uses
  `InputNonClientPointerSource.SetRegionRects` (WinAppSDK 1.8's supported
  API) with `SetDragRectangles` as a logged fallback; verify drag feel on a
  real Windows box.
- **Windows startup verification**: the chrome/Auth0 hardening turns the
  1.8 stowed-exception crash into logged, survivable failures — needs a
  launch check on the affected machine (use `VARDYPARTY_NO_CHROME=1` to
  bisect if anything still misbehaves).

## Risk register

| Risk | Notes |
|---|---|
| .NET 11 **preview** SDK | GA expected November 2026; preview 7 used here. |
| MAUI-Avalonia is **Preview 1** | APIs and package names may change; desktop-only today, WASM promised. |
| Avalonia 12 is itself preview | The backend rides on it; the SkiaSharp pin will need revisiting each bump. |
| Package drift | All packages come from nuget.org today; if previews move to a nightly feed, `NuGet.config` needs the feed added. |
| Divergent renderers | The same XAML renders via native controls on Android but Avalonia on Linux; visual QA needed on both before the Android switch. |

## Migration roadmap (all steps landed on this branch)

1. ~~Shared homepage~~ — `VardyParty.HomeUi`, Linux head
   (`VardyParty.Desktop`), tested logic in `VardyParty.Presentation`.
2. ~~Android/Windows head adoption~~ — `VardyParty/` retargeted to net11,
   `HomeHostPage` hosts the shared homepage natively.
3. ~~Delete the WebView~~ — `BlazorWebView`, `wwwroot/`,
   `Components/*.razor` and all Blazor plumbing removed; iOS/Mac Catalyst
   boot the XAML homepage too.
4. ~~Retire `VardyParty.Linux`~~ — playback + Auth0 device flow moved into
   `VardyParty.Desktop`; the Avalonia-11 app and its duplicate homepage are
   deleted.
