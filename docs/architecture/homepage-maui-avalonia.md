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

- **Rows**: a vertical `CollectionView` of league rows. Each row's cards sit
  in a horizontal `ScrollView` + `BindableLayout` — **not** a nested
  `CollectionView`. Nested WinUI ItemsRepeaters are the `0x800710DD` /
  `0xc000027b` layout defect. Inner strips are not virtualized; that is
  acceptable for a handful of fixtures per league, not a 30-card row.
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
- **Animation, kept cheap**: pulsing live dot (opacity/scale; static on the
  TV class per the idle invariant below), card scale on hover/focus, a
  sheen sweep on pointer-over — transforms and opacity only, no per-frame
  layout.
- **League menu**: overlay bound to the existing `MenuViewModel`/
  `ILeagueFilterService` — checkbox per league, Show all, Reset to defaults.

### Adaptive layout

`HomeLayoutClassifier` picks one of **TV / Desktop / PhoneLandscape /
PhonePortrait** from window size + television idiom, and
`HomeLayoutMetrics` supplies concrete sizes (card size, badge size, brand
logo size, font sizes, paddings) which the XAML binds. TV gets 10-foot
sizing (300×160 cards after THREE field reports that 440×232, then
360×190, then 340×180 were oversized — ~5.8 cards per row and ~3.9 league
rows now fit a 1080p panel; type and badge sizes hold the revised 10-foot
floors, badge ≥ 50 / score ≥ 30, guarded by
`Metrics_TvKeepsTenFootReadabilityFloors`); phones get smaller cards and
tighter padding, portrait tighter still. The TV class also carries the
raster-budget flags described under "TV performance package" below
(`FlatCardChrome`, `StagedStripCards`, `FocusRingThickness`,
`FocusedCardLift`).

Two later field reports (Windows/Desktop) tuned the league header: the
league icon was raised to read as a proper mark next to the bold title
(`LeagueIconSize` TV 40 / desktop 34 / phones 28–26, ≥ ~1.6× the title font
size on every class), and `RowSpacing` became the inter-league gap applied
**above** each league header (`HomeLayoutState.RowMarginThickness`) so a
header binds visually to its own card strip — desktop 40, TV only 32
because TV rows stay deliberately tight (~3.5 rows on a 1080p panel,
guarded by `Metrics_TvCardsFitAGridOnA1080pPanel`).

**The layout class is seeded before the first frame renders.**
`HomeLayoutState` used to boot with Desktop metrics and only reclassify on
the first `SizeChanged` — after the first paint — so an Android TV rendered
one Desktop-sized frame and then every bound metric jumped to the Tv class
at once, which a field report described as the whole UI "zooming in" ~0.5s
after launch. Now `HomeView` seeds synchronously when its BindingContext
lands (hosts set it during page construction):
`HomeLayoutClassifier.ClassifyInitial(isTv, displayPixelW, displayPixelH,
density)` — the TV flag wins outright, otherwise the physical display size
is converted to DIPs and classified; unknown display info falls back to
Desktop (the old default, and what the headless Desktop smoke run hits).
The MAUI head hands its Leanback TV detection to the shared view via
`HomeView.KnownTelevision = MauiProgram.IsTv` before `InitializeComponent`,
and `HomeView.IsTelevision()` ORs that flag with the MAUI idiom so the
construction-time seed and every later `SizeChanged` reclassification agree
(a disagreement would reintroduce the first-paint jump). `SizeChanged`
still owns live reclassification (window resizes, phone rotation).

Relatedly, the header subtitle no longer reads "0 games" during startup:
the games feed is a `BehaviorSubject` seeded with `null`, so subscribing
delivers a null board immediately, and `HomeViewModel.Apply` used to format
that as "0 games" under the spinning crest. The subtitle is now
`HomeViewModel.LoadingSubtitle` ("Loading…") whenever no catalog has been
delivered (startup and after sign-out); an empty-but-delivered catalog
still legitimately shows "0 games" alongside the empty state.

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
  the MAUI `Focused` path (scale 1.09 + the focus ring — on TV a 5 px
  near-white `#E2ECFF` ring plus a subtle white lift of the card itself,
  elsewhere the quiet 3 px `#AFCBFF` ring)
  and the focus-tick sound (`UiSoundService.FocusMove` via
  `MatchCardViewModel.FocusMoved`, throttled to one per 40 ms), so whichever
  focus system fires, the card lights up and ticks. The MAUI-side
  `Focused`/pointer handlers remain for Windows/Desktop.
- **The focus transition is animated and allocation-light**: the ring is a
  pre-built overlay `Border` faded in/out (~130 ms, native `View.Alpha`) in
  step with the `ScaleTo` lerp. Nothing on the focus path mutates the MAUI
  `Shadow` or the card stroke — both force blur/layout re-renders that jank
  32-bit TV hardware. D-pad autorepeat coalesces: focus moves arriving
  within 200 ms of each other apply chrome instantly (no animation
  pile-up); the deliberate single move gets the full glide plus the sheen.
- A native `Click` listener fires the pick — a focused clickable Android
  view converts DPAD_CENTER/Enter into a click itself.
- The wiring follows the platform view (`HandlerChanged` + `Loaded` +
  `BindingContextChanged`), so handler-timing and RecyclerView recycling
  can never leave a card unfocusable. It is **never torn down on
  `Unloaded`** — see the materialization-path table below.
- On focus gained the card scrolls itself fully on-screen: the row
  `ScrollView` via a chrome-padded `ScrollToAsync` target
  (`TvFocusScrollMath.ComputeStripTarget`, posted on Android so the 1.09
  scale + ring are included; a just-materialized staged card with no
  post-layout bounds defers per frame until laid out) and the outer rows
  `CollectionView` via router-owned `SmoothScrollBy`. **Scroll-into-view
  has exactly one owner per axis**: on Android the native `FocusChange`
  handler owns the strip axis and the router owns the rows axis; the MAUI
  `Focused` handler scrolls only when the native TV bridge is not wired.

#### One D-pad owner: the activity, not the cards

D-pad direction keys are owned by `MainActivity.DispatchKeyEvent` →
`TvDpadFocusRouter.TryHandleActivityKey` — **before the view tree sees the
key**. `TvDpadFocusRouter`/`TvDpadStripWalk` implement the moves: down/up
land on the card in the adjacent row whose screen X is nearest the current
card (Netflix column memory), left/right walk to the adjacent card in the
strip with row-edge clamping, up from the first row targets the registered
header Menu button, and every move is a router `RequestFocus` followed by
our single animated chrome-padded scroll. For a card- or header-focused
direction key the activity consumes **unconditionally** (a clamped edge is
"no move", never "let Android try") — so Android's default focus search
never runs for card navigation, which eliminates the system navigation
click (`ViewRootImpl.performFocusNavigation` plays it unconditionally on
any default-search move) and the instant auto-reveal double-jump by
construction, on every rail, however a card was materialized.

Two dispatch-order facts make this the only safe design:

1. **`Activity.OnKeyDown` is too late.** The strips are
   HorizontalScrollViews, and their `dispatchKeyEvent` runs
   `executeKeyEvent → arrowScroll` for LEFT/RIGHT whenever the focused
   descendant declines the key — a hidden extra scroll owner that scrolls
   layout-rect-only (chrome clipped) and races the animated chrome-padded
   scroll. Only `DispatchKeyEvent` runs before it.
2. **Per-card key listeners are structurally losable.** The
   materialization-path table (documented on `MatchCardView.EnableTvFocus`):
   initial batch, staged chunk appends and row REBINDS all fire
   `HandlerChanged`/`Loaded`/`BindingContextChanged` and re-wire — but a
   RecyclerView-CACHED row re-attaches firing **none of them** (same
   platform view, same BindingContext, and MAUI's `Loaded` re-fire on
   Android re-attach is unreliable — `HomeView.OnUnloaded` documents the
   same field-verified gap). Any per-card teardown on `Unloaded` is
   therefore permanent for exactly those cards. This produced three rounds
   of field bugs; the key path no longer lives on cards at all, and the
   remaining per-card listeners (`Click`, `FocusChange`) are wired per
   platform view and never unwired on `Unloaded`.

The walk itself (`TvDpadStripWalk`) collects **shown focusable leaves**
under the row scroller (card roots use BlockDescendants), **descending
through scrollers and recyclers instead of collecting them**: platform
scrollers are natively focusable (AOSP `initScrollView()` calls
`setFocusable(true)`), and treating the walk root as an ordinary node made
the leaf collection return `[the scroller]` — the focused card was never
found, left/right silently fell through to `arrowScroll`/default search
(the third-round field bug). Container hardening
(`TvDpadFocusRouter.HardenContainers`, applied on every card wiring pass)
also sets `Focusable=false` + `SoundEffectsEnabled=false` on every
container up to the rows RecyclerView, so a scroller can never be a
phantom focus stop. `TvDpadStripWalk`'s unit tests model scrollers as
focusable, matching the real tree; full traversal against the real
MAUI/Android handler tree remains device-only coverage.
`TvDpadActivityRouting` is the pure, unit-tested decision table for the
activity stages (dispatch: card/header ownership + trap seal for focus
stranded outside the open panel; OnKeyDown fallback: trap seal for keys the
panel items did not consume).

Defense in depth that stays but is no longer load-bearing:
`SoundEffectsEnabled=false` on cards and containers, and
`RevealOnFocusHint=false` on cards (suppresses the platform scroller's
instant `requestChildFocus` reveal for any focus change the router did not
initiate).

#### Focus chrome vs. ancestor clipping

Field evidence (real box): focused rings still clipped on **short rails
with no scrolling at all** — so the residual clipping was not scroll-target
math (that only runs when a scroll is needed) but **bounds clipping**: the
focus chrome (+9% scale over ~130 ms, 5 px `#E2ECFF` ring) renders outside
the card's layout slot, and Android ViewGroups default to
`clipChildren=true` (scroll views additionally clip to their padding), so
the chrome was sheared wherever the card touched a container edge. Two-part
fix:

1. **Un-clip the ancestor chain.** `TvDpadFocusRouter.HardenContainers`
   (the same per-card-wiring-pass ancestor walk — initial batch, staged
   appends and recycled rows are all covered the moment any card wires)
   now also sets `ClipChildren=false` + `ClipToPadding=false` on every
   container from the card's parent up to and including the rows
   RecyclerView. The chain, by inspection of the platform tree:

   | Container (bottom → top)                     | Platform view              | What its default clip sheared                      |
   |----------------------------------------------|----------------------------|----------------------------------------------------|
   | card wrapper (`MatchCardView` ContentView)   | `ContentViewGroup`         | scale/ring immediately beyond the card's slot      |
   | strip inner layout (`HorizontalStackLayout`) | `LayoutViewGroup`          | chrome past the strip content box                  |
   | strip scroller (row `ScrollView`)            | `MauiHorizontalScrollView` | clipChildren **and** clipToPadding (viewport edge) |
   | row container (`VerticalStackLayout`)        | `LayoutViewGroup`          | chrome at the strip's top/bottom edge              |
   | rows item wrapper (CollectionView item)      | `ItemContentView`          | chrome at the row item bounds                      |
   | rows list (`CollectionView`)                 | `RecyclerView`             | chrome at the recycler bounds (walk stops here, inclusive) |

2. **Reserve real room.** Clipping off cannot conjure space at the screen
   edge or against opaque siblings, so the strip reserves the chrome's room
   in layout: `HomeLayoutState.StripPaddingThickness` (start/end room for
   first/last cards; `ClipToPadding` stays off natively so cards still
   scroll edge-to-edge) and `RowHeight` (vertical headroom) are **derived**
   from `TvFocusScrollMath.FocusChromePadding` =
   `ceil(FocusChromeOverhead)` — the same constants the scroll targets use,
   never a separate magic number. The previous flat 12 dp vertical headroom
   covered the 7–8 dp scale overflow but **not the ring on top of it**
   (16.65 dp at the TV metrics → 17 dp/side now; horizontal 23 dp/side).
   Uniform across layout classes: every class renders the same 1.09
   focus/hover scale, and each class's own card size and ring thickness
   feed the derivation, so Desktop/phone stay proportionate.

Checked, no change needed: no MAUI-level `IsClippedToBounds` exists on the
strip/row templates (nothing re-clips what Android now allows), and nothing
on the focus path uses elevation — the TV card is `FlatCardChrome` (no drop
shadow), the focus lift is a veil *opacity* and the ring is a plain stroke
— so there is no elevation-based outline clipping of the ring either.

Acceptance (device): focus any card on a short rail — the ring must be
fully visible on all four sides, including the first/last card of the rail
and cards in the top row.

- One-shot autofocus: `HomeViewModel` arms `RequestsInitialFocus` on the
  first card of the first row on the empty→non-empty edge; the view consumes
  it once and calls `RequestFocus()` on the native view, so the app opens
  with a visibly focused card. Later refreshes never steal the highlight.

Key routing: `RemoteKeyHandler` (activity level) still has **no D-pad
direction cases** — direction keys are owned by the dispatch-stage router
above; `RemoteKeyHandler` only consumes media keys, Menu, Back and
(conditionally) Enter. `Activity.OnKeyDown` logging a `DpadUp/Down/...`
press now means the dispatch-stage router declined it (non-TV, non-board
focus, or the open menu panel's items own it).

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
- **Animation**: while `HomeViewModel.IsContentLoading` the crest spins on a
  3D `RotationY` turntable (coin-edge rim + angle-driven glint). When the
  first catalog lands, the spinner **finishes the turn it is on** and eases
  to face-on rest with a sheen — catalog paint must not `AbortAnimation`
  mid-rotation (that froze the crest edge-on). If layout kills the spinner,
  `HomeView` tells the logo after apply and it settles from the last angle.
  After rest, a slow ambient shimmer loop (a low-opacity sheen crosses the
  crest for a quarter of a 6 s loop) — **suspended entirely on the TV class**
  (`HomeIdleAnimationPolicy.AllowAmbientCrestShimmer`; the idle TV homepage
  must schedule zero recurring animation work) — and a subtle scale + glow +
  sheen response when TV focus enters the header (the Menu button). Same
  performance discipline as the cards — opacity/transform only, everything
  aborted on unload.
- **Lifecycle decisions** live in `BrandCrestSpinMachine`, a pure,
  clock-injectable state machine (extending the `BrandCrestSpin` helpers);
  `BrandLogoView` executes the returned steps and feeds animation facts
  back in, so the rules below are unit-tested headless:
  - **Restart after a layout abort is deferred, never dispatched.** On
    Windows the restart rides `HomeView`'s 50 ms apply pump
    (`PumpRequested`/`PumpCrest`, pump runs while `HasPendingCrestWork`);
    `Dispatcher.Dispatch` from the animation-finished callback was the
    same layout-adjacent-queueing class that stowed
    0x800710DD/0xc000027b. Android/Desktop restart via a posted
    continuation, matching the catalog's MainThread flush.
  - **Settle is guaranteed without any `IDispatcherTimer`** (Android TV
    starves timers under Choreographer load). `CatalogApplied` queues the
    settle **only when the apply carried API data** — "ready" strictly
    means API games are present, so the BehaviorSubject's null seed and
    empty pre-API boards keep the crest spinning (they used to settle it
    onto a "0 games" board). When content IS ready, a live turn stops
    repeating and settles from its own cycle-completion callback, a
    layout-killed spinner settles immediately, an aborted ease retries
    from the deferred tick, and a settle still unresolved after
    `SettleOverdueMs` (turn + ease + slack) **snaps** to face-on with
    direct property writes layout cannot abort. Invariant: once content
    is ready the crest always reaches face-on rest, on every platform.
  - **The settle never reverses through the coin-edge**: angles at or past
    180° ease forward to 360° (`RestTargetDegrees`); exactly 180° — the
    edge-on freeze case — rests at 360, not 0.

### UI sound design

Six generated WAV cues (`VardyParty/Resources/Raw/Sounds`) played through
`UiSoundService` (`VardyParty.Presentation`): navigation blip on TV focus
moves (rate-limited), select confirmation, stream-ready, error, goal chime
(via `MatchEventDetector`, see the match-event section below) and app-open
sting. Platform players: `SoundPool` on Android, `MediaPlayer` on Windows,
SoundFlow (miniaudio) on the Desktop head — which disables itself cleanly
when no audio device exists (headless CI). Sounds are suppressed while the
native video player is visible
(`INativeVideoPlayerService.PlaybackVisibilityChanged`) and can be turned
off in the menu's Settings section (persisted per platform).

### Match-event notifications

Field report: the goal sting fired with no visual attribution ("3 notes, no
idea what it means"). Match events now get a full delivery pipeline, all in
shared code:

- **`MatchEventDetector`** (`VardyParty.Presentation`, pure, unit-tested):
  fed the FILTERED display list on every catalog apply (games in hidden
  leagues are never observed), it emits GOAL (with the new score and which
  side scored), EXTRA TIME and PENALTIES (phase transitions INTO those
  phases via `MatchStatusPresenter.GetPhase`). A game's first observation
  never fires — first load, a fixture appearing mid-match, or a league
  being unhidden stays silent. Score corrections downward are ignored.
- **`MatchEventNotificationPolicy`** (unit-tested decision table): heads
  wire window lifecycle into `IsAppForegrounded` (Activated/Resumed =
  foreground, Stopped = background; Deactivated — focus lost while still
  visible, e.g. the desktop head's native VLC window taking focus —
  deliberately still counts as foreground) and the existing
  playback-visibility signal into `IsPlaybackActive`.
- **Homepage toast** (`MatchEventToastViewModel` + `MatchEventToastView`):
  a compact banner — league icon + league name, both team badges (monogram
  fallback), "GOAL — Jablonec 2–1 Rangers", tinted with the two teams'
  `TeamPalette` colours as the same cheap 2-stop wash the flat cards use.
  Top-right within the safe area on Desktop/TV, bottom-centre on phones.
  Auto-dismisses after ~4s with a short slide/fade; every animation is
  event-driven and finite (TV idle invariant), and the whole overlay is
  input-transparent and never focusable (the TV D-pad cannot land on it).
  Near-simultaneous events queue sequentially, at most 3 deep behind the
  showing toast (drop-oldest beyond that); the queue/dismiss state machine
  is clock-injectable and unit-tested, with token-guarded dismissal so a
  superseded presentation can never dismiss the wrong toast.
- **Card flash**: when the event's card is materialized, a ~1.5s finite
  render-only flash (score-label pop + the card's own team wash pulsed
  brighter) runs synchronized with the toast. Transform/opacity only — no
  stroke, shadow or layout mutation.
- **`MatchEventBus`**: delivered events (the ones that passed the policy)
  are published on an in-process bus. The homepage toast is today's only
  consumer; toasts over the native video players (next dispatch) subscribe
  to the same stream instead of growing their own detector.

Delivery matrix (surface × behaviour), gated by two Settings toggles:

| App state | Surface | Sting (audio) | Toast | Card flash |
| --- | --- | --- | --- | --- |
| Foreground | Homepage (no stream) | Yes | Yes | Yes |
| Foreground | Stream playing | No | Yes | Yes (behind player) |
| Background / minimized | any | No | No — dropped, no catch-up on resume | No |

- **"Goal notifications"** (default ON, persisted via the
  `ISoundPreferencesStore` pattern): OFF suppresses sting + toast + card
  flash entirely.
- **"UI sounds"** (existing toggle): still governs navigation ticks
  independently, and still gates the sting's AUDIO when notifications are
  ON (the sting routes through `UiSoundService`, which also keeps its
  playback suppression).

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
   reference build). Heads referencing `VardyParty.HomeUi` therefore override
   `GlobalPropertiesToRemove` so the head TFM flows in (windows/android).
   `VardyParty.Desktop` (and HomeUi.Tests) also set `AdditionalProperties`
   `TargetFrameworks=net11.0`. Linux HomeUi lists `net11.0;net11.0-android`
   so the MAUI `--no-restore` Android job has that TFM in assets. Unit tests
   set `HomeUiTargetFrameworks=net11.0` (CI job env) so they never restore
   android.
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
4. **Catalog apply must not `Dispatcher.Dispatch` from Rx into WinUI**:
   queue on `HomeViewModel`, drain on the UI thread. Windows: idle
   `IDispatcherTimer`. Android/Desktop: `MainThread` (TV Choreographer
   skips starve the timer). Hosts must not flush from the subscription.

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
  via `WindowsEventLogger.Fatal`, and leaves `e.Handled = false` so a
  remaining uncaught XAML-thread throw is still visible rather than
  swallowed.
- **Serialized + coalesced games publishes** (`EnrichedGameService`): the
  API and BBC pollers each ended in `RunMatching` → `_subject.OnNext` with
  no synchronization, so `GamesStream` could emit from two threads at once,
  and the two startup fetches published two full boards ~1s apart — a full
  board reset mid-first-materialization used to hit nested CollectionViews
  (WinUI's documented 0x800710DD failure mode). The shared catalog is no
  longer nested Repeaters; coalescing still avoids two full boards ~1s
  apart at startup. A private publish lock now serializes the whole match+publish
  body, and the startup contract is **enriched-first**: every API publish is
  held until the initial BBC completion (success or failure) delivers the
  one enriched board; a 30s valve from polling start
  (`EnrichedGameService.InitialEnrichmentValve`) releases the freshest
  API-only board if BBC hangs (TV multi-day parse routinely exceeded the
  old 10s valve and leaked scoreless games that then reshuffled). BBC
  failure releases immediately. A null or empty pre-API board is never
  delivered as a settled state. Steady-state live-score publishes are never
  delayed (no debounce; the hold is spent once the first board is out).
- **Queued UI apply** (`HomeViewModel` / `HomeView`): catalog, errors, and
  badge assigns are queued off the Rx/HTTP thread. Windows drains that
  queue from a UI-thread `IDispatcherTimer` (must not `Dispatcher.Dispatch`
  apply into WinUI layout). Android and Desktop flush via
  `MainThread.BeginInvokeOnMainThread` — TV startup often skips hundreds of
  Choreographer frames, so the idle timer never ticks and the crest would
  spin forever. Hosts only call `UpdateGames`; `HomeView` owns the drain.
  The crest's deferred work (restart after a layout abort, settle
  retries/snap) rides the same channels: the Windows pump keeps ticking
  while `BrandLogoView.HasPendingCrestWork`, and Android/Desktop use
  posted continuations — the crest never owns a timer.
- **Single-threaded goal detector** (`HomeViewModel`): the
  `ScoreChangeDetector`'s plain `Dictionary` is now only touched inside
  `FlushPendingApply` (and `ResetScoreObservations` queues onto that path),
  so it is single-threaded by construction rather than by the publisher's
  lock discipline.
- **No sync-over-async on the dispatcher** (`MauiHomeAssetLocator`):
  `ResolveLeagueLogoPath` blocked on `OpenAppPackageFileAsync` with
  `GetAwaiter().GetResult()` and is reached on the UI thread at first
  render; `IHomeAssetLocator.ResolveLeagueLogoPathAsync` is now genuinely
  async (both the MAUI and Desktop locators updated).
- **Sounds in the AppX layout**: `SyncSoundsToWindowsLayout` (csproj)
  mirrors the league-logos target so `Resources\Raw\Sounds\**` lands in
  both the win-x64 output root and `AppX\Sounds\` on VS/dotnet-driven
  incremental syncs; `run-windows-debug.ps1`'s recursive sync already
  covered script-driven deploys. The loose-file `@(Content)` items backing
  this are conditioned to the windows TargetPlatformIdentifier: on Android
  the WAVs ship solely as MauiAssets (`assets/Sounds/*.wav`, loaded via
  `Assets.OpenFd`) and an unconditioned Content item earned one XA0101
  "build action not supported" warning per file.

## TV performance package (field-driven, 32-bit cortex-a9 box)

Field evidence from a real Android TV (~37 games / 17 live): Choreographer
skipped 79–315 frames in a steady ~1.4s rhythm **while the homepage idled**,
indefinitely. Root cause validated in code: ~17 infinite live-dot pulse
animations plus the crest's ambient shimmer each tick the MAUI animation
manager every frame; every tick invalidates, and with ~37 fully-materialized
BindableLayout cards (each with a composition shadow + 4 badge shadows + a
diagonal 4-stop gradient) one pass took ~1.3s on the single weak core — the
next tick was already queued, so the loop never drained. On top of that,
every ~60s poll Clear+rebuilt all rows and cards. The fixes, in order:

- **TV idle invariant** (`HomeIdleAnimationPolicy`, unit-tested): on the
  `HomeLayoutClass.Tv` class NOTHING schedules recurring animation work
  while the homepage idles. The live dot is a static treatment (no pulse),
  the crest's ambient shimmer is suspended (sheen only on header focus
  change), and the crest's loading spin stops all ticking once settled
  (already timer-less). Every animation on the homepage is now either
  event-driven and finite (focus glide, sheen sweep, settle) or gated off
  on TV. The resolving pulse on a picked card is the single sanctioned
  loop, and only while stream resolution is in flight.
- **Diff-based in-place updates + sticky ordering** (`HomeBoardDiffer`,
  pure + unit-tested): polls update existing card VMs' INPC properties in
  place; rows keep their positions except when the set of live leagues
  actually changes; the row holding the focused card NEVER moves; card
  order within a row is stable. Materialized card views and loaded badges
  survive every poll.
- **Enriched-first initial reveal**: see the coalescing bullet above (10s
  valve; "ready" = API data present; empty applies keep the crest spinning
  and the subtitle on Loading…).
- **Flat TV card chrome** (`HomeLayoutMetrics.FlatCardChrome`): TV cards
  drop the card + badge composition shadows (slightly stronger border
  instead) and use a horizontal 2-stop team wash instead of the diagonal
  4-stop one. Desktop/phone visuals unchanged.
- **Staged strip materialization** (`HomeLayoutMetrics.StagedStripCards`,
  TV: 8): rows are already virtualized by the outer CollectionView
  (RecyclerView), but a BindableLayout strip materializes every card at
  bind — so a new row over the budget starts with its first 8 cards and
  appends the rest in chunks of 4, one per dispatcher message. A newer
  apply supersedes staged work (epoch-pruned; the diff inserts whatever is
  still owed).
- **Back = close, never exit** (`HomeBackDecision`, unit-tested): with any
  overlay registered (menu, device-code sign-in, stream resolution) Back
  delegates to the overlay chain; after an overlay consumes Back, app-exit
  Backs are ignored for a 1.5s grace (a repeat press against a stale frame
  on the saturated main thread must not exit). `RemoteKeyHandler` isolates
  every subscriber (per-delegate catch + log) because `OnKeyDown` has no
  exception guard — a throwing close handler used to be an app crash.
- **10-foot focus chrome**: TV focus ring is 5px near-white plus a subtle
  white veil (0.10) lifting the focused card; all transitions remain
  opacity/transform-only. TV cards took a third size notch to 300×160
  (badge 50, score 30) for ~5.8 cards per row on 1080p.

## Phone polish package (field-driven, Android touch testing)

- **Appends yield to interaction**: staged strip chunk appends could land
  during an active touch drag and hitch the strip. Any strip scroll event
  pauses the appends; a one-shot cooldown (restarted per event — it fires
  once after the last scroll callback, no recurring tick) resumes them and
  re-kicks the pump. Epoch pruning is unchanged.
- **Phone size notch**: cards went one notch down on both phone classes
  (272×150 landscape, 244×140 portrait; badge/score/team proportional with
  arm's-length floors), landed as an isolated revertable commit like the
  TV notches.
- **Flat chrome on phones**: `FlatCardChrome` now covers TV + both phone
  classes. The TV evidence transfers — the card + badge composition
  shadows are the biggest raster line-item at ~37 cards on any renderer,
  phones spend their GPU headroom on 60fps touch scrolling instead, and
  flat keeps phones visually consistent with TV. Desktop keeps the full
  treatment (few visible cards, real headroom, 2-foot viewing distance).

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
- **Inner BindableLayout strips** are not virtualized (fine for a handful of
  fixtures per league, not a 30-card row).

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
