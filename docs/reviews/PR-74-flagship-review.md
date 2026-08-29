# PR #74 — Flagship code review (v2.0.0 bar)

Cross-platform C# / .NET MAUI review against a **v2.0.0** release standard.
Posted as a branch doc because this environment cannot call `addComment` /
`ManagePullRequest` on the GitHub PR.

**PR:** https://github.com/Vardy-Party/Client/pull/74  
**Branch:** `cursor/maui-avalonia-homepage-1e7d` → `main`

## Verdict

**Not ready to label 2.0.0 today**, mostly for cheap fixes — not because the
hard TV/homepage work is wrong. Pure `VardyParty.Presentation`, shared
`HomeUi`, and Android TV D-pad ownership are genuinely impressive. Gaps:
display version still `1.7.x`, README first impression, Desktop “Finding
streams” overlay flash, duplicated host glue, and Apple platforms without
hardware QA.

## Blockers

1. **Release is not versioned 2.0.0** — `Version.props` still has
   `ApplicationDisplayVersion` `1.7.116`. Use **Bump Display Version**
   (`major`) on `main` per
   [agent-playbook-merge-client-pr-v2.md](../agent-playbook-merge-client-pr-v2.md)
   so the next CD packages `2.0.0`.
2. **README historically described a chess app** — “Tournament Discovery /
   pairings / player profiles” contradicted the product. Branch tip has been
   fixing README highlights / Apple untested / chess line — confirm tip.
3. **Linux: “Finding streams” overlay hides on first pick** —
   `DesktopHomePage` subscribes to the orchestrator `BehaviorSubject` and
   treats the initial `IsResolving=false` as hide. MAUI `HomeHostPage`
   documents `_resolveOverlayOpen` specifically to avoid this; Desktop lacks
   that guard → overlay flashes then vanishes until a later progress emission.
4. **Homepage XAML is shared once; host orchestration is copy-pasted twice** —
   `HomeHostPage.xaml.cs` ≈ `DesktopHomePage.xaml.cs` (~26 same-named private
   methods). Divergence already produced blocker #3. Prefer a
   `HomeHostController` (or similar) in `Presentation` with heads as thin
   overlay renderers.
5. **PR still DRAFT; own acceptance list unverified** — black bar / TV first
   rail / ticks / WSL audio+Close. iOS/Mac Catalyst: CI builds only; do not
   claim full support without Apple Developer Account QA (README tip marks
   them pending).

## Should fix before labeling 2.0.0

6. **CI Code Quality excludes `HomeUi` + `Desktop`** from
   TreatWarningsAsErrors / format — largest new surface unenforced;
   `SampleGames.cs` already fails `dotnet format --verify-no-changes`.
7. **Stale docs** — ceremony / `VardyParty.Linux` / outdated version prose;
   living docs should be homepage architecture + accurate versioning.
8. **No design tokens** — ~83 hard-coded hex colours in shared XAML.
9. **Accessibility / localization thin** on the new UI.
10. **TV UI sounds: blind 18s + 5s sleep** — contradicts clock-free TV
    discipline; hook crest/board-ready instead.
11. **Android Back has multiple owners** — funnel like D-pad via one decision
    path.
12. **Silent `catch { }`** on Back / remote wiring / some Desktop sound paths.
13. **No central package management** on a preview stack.
14. **Fire-and-forget image loading** without try/catch on `LoadImagesAsync`.
15. **Desktop tests thin** vs player/home page size.

## What’s already impressive

- Real layering: `Presentation` is plain `net11.0`, heavily tested, no MAUI refs.
- Hard problems solved properly: `BrandCrestSpinMachine`, TV D-pad at
  `DispatchKeyEvent`, idle animation policy, LibVLC abandon/timeout on Desktop.
- Blazor + `VardyParty.Linux` deleted (not dormant).
- `docs/architecture/homepage-maui-avalonia.md` is reference-quality.

## README / 2.0.0 front door checklist

- State **v2.0.0** intent; link key docs (architecture, Linux/WSL, playback,
  versioning, merge playbook).
- Honest platform matrix (Verified vs CI-only Apple).
- Quick start for .NET 11 preview + `maui-tizen` /
  `HomeUiTargetFrameworks=net11.0`.
- Screenshots / four-up later if possible.

## Suggested merge order for v2.0.0 (human-driven)

1. **Bump Display Version** → `major` on `main`, **untick** build-number bump
   → `2.0.0` on main.
2. Approve + squash-merge this PR (keep product title; do **not** prefix
   squash subject with `ci:`).
3. Watch CI → Increment Build Version → CD → confirm release `2.0.0-bN`.

Playbook: [agent-playbook-merge-client-pr-v2.md](../agent-playbook-merge-client-pr-v2.md)
(playbook only until human approval).

## Stance

Approve the architecture direction; **hold the 2.0.0 label** until blockers
1–5 (and ideally CI exclusion + Desktop overlay) are addressed or consciously
deferred in the PR description.
