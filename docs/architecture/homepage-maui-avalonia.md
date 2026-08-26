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

Today the VardyParty app **bypasses all of that**: `MainPage.xaml` hosts a
single `BlazorWebView`, and the whole homepage is HTML/CSS running in a
WebView (`Components/Pages/Home.razor`, ~968 lines). On a 32-bit Android TV
box that means an embedded browser engine doing layout, style and JS on
hardware that struggles with it — which is exactly the sluggishness we see.
Replacing the WebView with real MAUI XAML (`CollectionView` and friends) is
the performance fix: virtualized native views instead of DOM.

### How does .NET on Linux fit with XAML?

It doesn't, out of the box. There is **no Microsoft UI stack for Linux**:
WPF and WinUI are Windows-only, and MAUI has never shipped a Linux target.
.NET itself runs fine on Linux (our domain libraries and `VardyParty.Linux`
prove it) — what was missing is the UI layer.

**Avalonia** fills that gap: it is a XAML framework that does not use native
controls at all. It draws every pixel itself with Skia (the same graphics
library Chrome and Flutter use), so the same UI runs anywhere Skia runs —
including Linux. The existing `VardyParty.Linux` app is Avalonia 11 with its
own hand-written window; its XAML dialect is *similar* to MAUI's but not
compatible, which is why the app previously needed two homepage
implementations.

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
VardyParty.HomeUi/            shared MAUI XAML homepage (net11.0 class library)
  Views/HomePage.xaml         Netflix-style rows + league menu overlay
  Views/MatchCardView.xaml    rich match card (badges, score, status, effects)
  ViewModels/                 HomeViewModel, LeagueRowViewModel, MatchCardViewModel,
                              LeagueToggleViewModel, HomeLayoutState
  Services/                   IBadgeImageLoader (+ Svg.Skia rasterizer), IHomeAssetLocator

VardyParty.Desktop/           Linux/desktop head (net11.0, UseAvaloniaApp)
  MauiProgram.cs              AddVardyParty + AddVardyPartyHttpClients + HomeUi DI
  Services/HomeFeed.cs        binds HomeViewModel to EnrichedGameService.GamesStream
  Services/StubAuthTokenProvider.cs   auth stub (see "Stubbed", below)
  Services/SampleGames.cs     VARDYPARTY_DESKTOP_SAMPLE_DATA=1 offline data

VardyParty.Presentation/Application/Home/
  TeamPalette.cs              curated club colours + deterministic HSL fallback
  MatchStatusPresenter.cs     phase/chip/score/aggregate/kick-off formatting
  HomeRowsBuilder.cs          league-row grouping and ordering (live rows first)
  HomeLayoutClass.cs/.Metrics HomeLayoutClassifier: TV/Desktop/PhoneLandscape/Portrait
```

The pure logic lives in `VardyParty.Presentation` (net10.0, fully
unit-tested in `tests/VardyParty.Presentation.Tests`) so the existing MAUI
app can adopt it without touching the preview stack.

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
`HomeLayoutMetrics` supplies concrete sizes (card size, badge size, font
sizes, paddings) which the XAML binds. TV gets 10-foot sizing and relies on
MAUI's focus visuals for D-pad navigation; phones get smaller cards and
tighter padding, portrait tighter still.

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
   `TargetFramework` off every `ProjectReference` (to protect net10.0 domain
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
3. **Code Quality CI runs .NET 10**: net11.0 projects neither build nor
   `dotnet format`-load under SDK 10, so they are excluded there and covered
   by the dedicated `build-desktop-preview` job (build + xvfb startup smoke
   test with sample data).

## Stubbed / follow-ups

- **Auth**: `VardyParty.Desktop` registers `StubAuthTokenProvider` (always
  unauthenticated). The games API answers 401 and the homepage shows its
  error banner; `VARDYPARTY_DESKTOP_SAMPLE_DATA=1` renders the full UI
  offline. Follow-up: reuse the Auth0 device-code/PKCE flow already in
  `VardyParty.Auth`/`VardyParty.Linux`.
- **Playback**: picking a card raises the game-selected intent but playback
  is not wired (`VardyParty.Linux` keeps LibVLCSharp for now).
- **Android head adoption**: retarget `VardyParty/` to net11.0, add
  `net11.0-android` to HomeUi, replace `MainPage.xaml`'s BlazorWebView with
  `HomePage`. Deliberately not in this PR — it would put the whole product
  on preview SDKs before the stack proves out.

## Risk register

| Risk | Notes |
|---|---|
| .NET 11 **preview** SDK | GA expected November 2026; preview 7 used here. |
| MAUI-Avalonia is **Preview 1** | APIs and package names may change; desktop-only today, WASM promised. |
| Avalonia 12 is itself preview | The backend rides on it; the SkiaSharp pin will need revisiting each bump. |
| Package drift | All packages come from nuget.org today; if previews move to a nightly feed, `NuGet.config` needs the feed added. |
| Divergent renderers | The same XAML renders via native controls on Android but Avalonia on Linux; visual QA needed on both before the Android switch. |

## Migration roadmap

1. **This PR** — shared homepage (`VardyParty.HomeUi`), Linux preview head
   (`VardyParty.Desktop`), tested logic in `VardyParty.Presentation`,
   preview CI job. Old apps untouched.
2. **Android head adoption** — retarget `VardyParty/` to net11 (when the
   stack and our confidence allow), host `HomePage` natively, measure on the
   armeabi-v7a TV box.
3. **Delete the WebView** — remove `BlazorWebView`, `wwwroot/`,
   `Components/*.razor` once the XAML homepage is the shipped UI.
4. **Retire `VardyParty.Linux`** — move playback + Auth0 device flow into
   `VardyParty.Desktop`, then delete the Avalonia-11 app and its duplicate
   homepage.
