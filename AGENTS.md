# Agent notes — VardyParty Client

This is the standing brief for agents working in this repo. Detailed playback
policy lives in `docs/STREAM_PLAYBACK_RULES.md`; architecture and build docs
are indexed in `docs/INDEX.md`. Copilot/test hard rules are in
`.github/copilot-instructions.md` and `.cursor/rules/csharp-tests.mdc`.

## GitHub identity for this repo

**Before any `git` push/fetch/pull or `gh` against this repo**, Read and follow
the personal skill `github-noninteractive-auth`
(`C:\Users\jonbr\.cursor\skills\github-noninteractive-auth\SKILL.md`). Never
trigger Git Credential Manager (popups do not name the repo/agent). Never
`gh auth switch`. Resolve the Vardy-Party token in-process only; clear it after.

Use **`GH_TOKEN`** (Vardy-Party PAT / `gh auth token --user Vardy-Party`) as the
`gh` identity for pull requests and other GitHub API work on this repository.
Do not use the default Cursor `gh` account or another owner’s leftover
`$env:GH_TOKEN`; those are not collaborators here.

| Secret | Purpose | Scopes |
| --- | --- | --- |
| `GH_TOKEN` | PAT for `gh` / PRs / repo API | `repo`, `read:org` |
| `NUGET_GITHUB_TOKEN` | GitHub Packages restore only | `read:packages` |

`NUGET_GITHUB_TOKEN` cannot open pull requests. Prefer `GH_TOKEN` whenever `gh`
needs repo access. The Packages username secret (`NUGET_GITHUB_USERNAME`) is
the same GitHub login `GH_TOKEN` must belong to.

Commit author for this repo is **Vardy-Party** (`vardyparty@jonbreen.uk`). Stage
**explicit paths only**. Never force-push. Canonical checkouts only — no
worktrees. Never delete files without an explicit yes. Do not merge or deploy
unless asked. "Deploy" here means installing a build on Jon's devices.

## M3U8-resolver / LocalService NuGet packages

When restoring **LocalService** (or Client Streaming strategy packages), always use the
**newest** `VardyParty.LocalService.*` package versions available from:

1. Sibling `../Strategies/artifacts/nuget` (`strategies-local` in NuGet.config), and/or
2. GitHub Packages (`NUGET_GITHUB_TOKEN` + `NUGET_GITHUB_USERNAME` on
   `Vardy-Party/M3U8-resolver` — CI injects these; local agents need the same values
   in the shell env or a user-level NuGet credential for `github-vardy-party`).

Do **not** leave stale pins (e.g. V2.Scrape `0.1.1`) when newer packs exist — outdated
scrape plugins advertise `mp.chrome` but fail `/mp` with `v2 scrape plugin not registered`.

Never commit `packageSourceCredentials` / clear-text PATs into repo `NuGet.config`.
Never overwrite a published nupkg version. Never push MpScrapeGate.

## API preview deploy

The API preview Worker is connected to GitHub. Pushing an API pull-request branch deploys `headless-m3u8-preview` through Cloudflare Workers Builds. Do not follow that push with a local `npm run deploy` or `wrangler deploy`. Uncommitted API files are not on the worker until they are committed and pushed. See `AGENTS.md` in the API repo.

## ⚠️ `GH_TOKEN` expires every ~90 days — rotate it

`GH_TOKEN` is a GitHub Personal Access Token issued **2026-09-02**. It
**expires about 90 days later (around 2026-12-01)**. After that, `gh` and PR
creation fail with auth / collaborator errors.

To rotate:

1. Create a new GitHub PAT as that same Packages username, with **`repo`** and
   **`read:org`** (GitHub → Settings → Developer settings → Personal access
   tokens).
2. Update **`GH_TOKEN`** in the Cloud Agent Secrets panel.
3. New Cloud Agents pick up the new value automatically; no code change needed.

Set a calendar reminder a few days before **2026-12-01**.

## Toolchain

- **SDK:** .NET 11 GA. The repo `global.json` pins `11.0.100` with
  `rollForward: latestPatch` (any 11.0.1xx patch). CI, CD and release use
  `DOTNET_VERSION: "11.0.1xx"`, and every platform job installs workloads with
  `--version 11.0.100.1` (workload set). Linux must use this SDK too — see
  `docs/LINUX_SUPPORT.md`.
- **Bumping the SDK or workloads:** change `global.json` `sdk.version`, the
  `DOTNET_VERSION` env in `ci.yml`, `cd.yml` and `release.yml`, and every
  `dotnet workload install ... --version <set>` in those three files together
  (including the Apple jobs, which also need a matching Xcode). Then install the
  same SDK on the MSI and box and run all test projects before pushing.
- **Apple CI:** iOS and macOS jobs select **Xcode 27.2**
  (`/Applications/Xcode_27.2.app`) and pin workloads with
  `dotnet workload install maui --version 11.0.100.1` (and `ios` /
  `maccatalyst` the same way). Do not change CI unless asked, and do not
  unpin those versions.
- **MSI shell:** PowerShell. There is no `rg` — use `Select-String`.
- Only one LocalService process at a time. Warn before restarting it, and do
  not touch it unless asked.

## Build, test, and CI watch

Run **all** test projects, not only the suite you changed. CI does the same:
it builds and tests every `tests/**/*Tests.csproj` (the helper
`tests/VardyParty.TestSupport` is not a test project). Locally, from the repo
root:

```powershell
Get-ChildItem tests -Recurse -Filter *Tests.csproj | ForEach-Object {
  dotnet test $_.FullName -c Release
}
```

On Linux/macOS: `find tests -name '*Tests.csproj' -print0 | xargs -0 -n1 -I{} dotnet test "{}" -c Release`.

After **every** push to a PR, watch that PR's GitHub Actions checks through to
completion and report every job, including ones that were already failing.
Do not assume a failure is "just CI" without comparing to a known-green run
and identifying the cause. Do not "fix CI" (workflow or runner changes)
unless asked.

Do not hand-edit `ApplicationDisplayVersion` on a feature branch.
`ApplicationVersion` is the integer build counter. See
`docs/VERSION_MANAGEMENT.md`.

## Secrets and files that must never be committed

Builds may patch source files. Always restore them afterwards, and never
stage them:

- `VardyParty/appsettings.json` and `VardyParty.Linux/appsettings.json`
  (Auth0 / API secrets). After any package or `-p:PatchAppSettings=true`
  build: `git restore VardyParty/appsettings.json` and, if touched,
  `VardyParty.Linux/appsettings.json`.
- `VardyParty/Resources/Splash/vardyparty_splash_generated.svg`
- A root `Program.cs`, if one appears

Never print Auth0 values, cookies, stream tokens, or other secrets from
appsettings, logs, DevFlow network captures, or health dumps. Fill blank
appsettings fields from a working install without printing the values.

## Device builds and installs

Do not build or install on devices until Jon asks. When he does, keep app
data (upgrade in place; do not uninstall first). Restore secrets and the
splash SVG after the build. Preferred commands:

- **Android / Android TV:** `pwsh ./package-android.ps1 -Api production`
  (or `-Api preview` / `-Api local` when asked). Then
  `adb install -r` to the TV (`192.168.1.108:5555` unless told otherwise).
  Launch `com.vardyparty/.MainActivity` or `.TvLeanbackAlias`. Keep a single
  logcat capture, by PID:
  `$pid = adb shell pidof -s com.vardyparty; adb logcat --pid=$pid`
- **Windows:** follow the current release-workflow `dotnet publish` (do not
  rely on `package-windows.ps1` if it still hits PRI175/PRI277). Sign with
  the current-user `CN=VardyParty` certificate; do not add certificates to
  machine stores. Back up settings, then upgrade with `Add-AppxPackage`
  (no remove) so data is kept.
- **Linux:** `docs/LINUX_SUPPORT.md`. Pin
  `-p:HomeUiTargetFrameworks=net11.0` per command; do not export that
  variable into the shell.

Use a signed MSIX. Verify the installed Windows build info and the TV
`versionCode` after a deploy, rather than assuming the new build is running.

## Stream playback (agent rules)

Normative detail and tests: `docs/STREAM_PLAYBACK_RULES.md`. Do not weaken
these without an explicit, documented exception and tests.

- **No repeated footage.** Never re-show already-watched segments (viewers
  call it "like the Matrix"). Do not serve a last-good / stale playlist, and
  do not reload a stuck playlist as an escape. Riding the live buffer
  forward until it runs out is fine; starting those segments again is not.
- **Retries first, then switch, then maybe return.** Mid-play playlist
  failures use option 1 (`RetrySameUrl`) in the shared fetch layer
  (`IPlaylistReloadRecoveryStrategy` / `PlaylistReloadCoordinator`) while the
  player rides its buffer. Hold a player request at most 2s
  (`PlaylistReloadHoldBudget`); give up a stream after 12s of failed reloads
  (`PlaylistReloadGiveUpBudget`). When retries cannot cover an outage,
  switch so there is always a live picture (default
  `GamesApi:PlaylistReloadStrategy` = `RetrySameUrlThenSwitchBack`).
- **Switch-back (option 2).** The left stream keeps its channel number and is
  not marked dead for CDN-unreachable. Re-probe only that row's playlist at
  5s, 15s, then 30s / 60s / 120s. After two consecutive healthy `#EXTM3U`
  200s, switch back immediately if its last known height is higher than the
  backup; otherwise wait out a 30s dwell on the backup. User Next /
  Previous, or picking a chip, cancels the return.
- **Uniform cross-platform strategies, thin adapters.** Shared recovery,
  switch-back, height settle (`DecodedPictureSizeSettler`), and the 8s
  no-picture check belong in Core. Platform files only report facts and
  talk to the engine. Do not add a platform-only recovery path that can
  replay footage or decide to switch on its own.
- **Tiers are for LocalService scheduling only** (High > Medium > finding;
  `docs/PROBE_SCHEDULING_RULES.md`). They do not change Next / Previous /
  auto-advance order.
- **Next walks channel numbers** in order, with wrap, skipping only rows
  tried or failed this cycle. Channel numbers are stable and append-only
  (a drop closes the gap). A later High or Medium row is appended and
  reached in number order.
- **Retry the same stream before switching** on an established stream.
  **CDN-unreachable is never dead** (Deferred, rechecked) — connection
  failures, 5xx, and similar are not `ConfirmedDead`. Running out of
  mid-play retries is unreachable, not a malformed playlist.
- **Next / Previous never marks a stream bad.** Reporting a bad stream is a
  separate action.
- **Overlay height:** clear the previous stream's measured size on switch.
  Do not take height from the playlist `RESOLUTION` or catalog field. Prefer
  a probed bitstream height, then the settled decoded size (1s unchanged).

Known engine limits that still differ (do not paper over them with
platform-only policy): only Android can seek to the live edge; Windows
`DownloadRequested` must answer within 8s; Linux LibVLC cannot sit a fixed
25s behind live (Linux stays at 4s). Windows also keeps a 20s startup
failsafe on top of the shared 8s picture check.

## .NET MAUI Labs tooling (once adopted)

These tools are experimental (`dotnet/maui-labs`, pre-release, not covered by
the Microsoft Support Policy). Do not install the CLI, add NuGet packages, or
run `maui ai init` (even without `--dry-run`) unless asked. They are not
required to build or test this repo today.

### MAUI CLI

- Package / command: global tool **`Microsoft.Maui.Cli`** (`maui`).
- Install (when approved), from a trusted directory, not from a repo with an
  untrusted `NuGet.config`:
  `dotnet tool install -g Microsoft.Maui.Cli --prerelease`
- Current published preview (6 Nov 2026): `0.1.0-preview.14.26555.2`.
- Host support: Windows, macOS, Linux. Apple Xcode/simulator commands are
  macOS-only. `maui doctor` diagnoses the environment; `--json` is for
  scripting; `--dry-run` previews fixes. Do **not** run `maui doctor` (it
  can auto-fix) or `maui android install` on the MSI without approval — the
  Android SDK / TV `adb` workflow is already in place.
- After install, prefer `maui doctor --dry-run` and `maui device list`
  before changing anything.

### DevFlow (in-app agent + MCP)

DevFlow lets an agent inspect a **running Debug build**: visual tree,
screenshots, taps, logs, HTTP monitoring, profiling, preferences, secure
storage, and sandboxed files. Official docs:
https://learn.microsoft.com/en-us/dotnet/maui/developer-tools/devflow/

**App changes (Debug only — never in Release / store / production APK or
MSIX):**

```xml
<PackageReference Include="Microsoft.Maui.DevFlow.Agent"
                  Version="0.1.0-preview.14.26555.2"
                  Condition="'$(Configuration)' == 'Debug'" />
```

In `MauiProgram.cs`:

```csharp
#if DEBUG
builder.AddMauiDevFlowAgent();
#endif
```

Do not add `Microsoft.Maui.DevFlow.Blazor` unless a Blazor WebView is
introduced. Do not set `MauiDevFlowIncludeProjectPath=true` in shared or
Release builds (it embeds the full project path). XAML source maps belong
in Debug only.

**Security:** The in-app agent is an HTTP server and must stay off the
Release binary. Official guidance is to wrap registration in `#if DEBUG`.
The agent binds loopback (`127.0.0.1`); the broker listens on
`localhost:19223` (Inspector at `http://localhost:19223/inspector/`) and
assigns agent ports from **10223–10899** (fallback **9223**). Even on
loopback it can read **secure storage**, intercept HTTP (stream URLs and
tokens), and take screenshots. Treat DevFlow captures like logs: never
print tokens, cookies, or Auth0 values. Do not leave the agent or WebView
remote-debugging ports enabled in a packaged production build.

**Platform reality for this app:**

| Head | Official DevFlow agent status | Notes |
| --- | --- | --- |
| Windows (WinUI MAUI) | In progress | Try here first, on a Debug run, before relying on it. |
| Android / Android TV | In progress | Needs `adb reverse` for broker `19223` and the assigned agent port, including physical devices. Untested on the TV over network adb (`192.168.1.108:5555`); use `adb -s <serial> reverse ...` when several devices are connected. |
| Linux (`VardyParty.Linux`) | Official Linux support is **GTK4**, not Avalonia | This head uses the Avalonia MAUI backend + LibVLC (`docs/LINUX_SUPPORT.md`). `Microsoft.Maui.DevFlow.Agent.Gtk` is for `Microsoft.Maui.Platforms.Linux.Gtk4`. Do not assume DevFlow works on Linux until proven. |
| iOS / Mac Catalyst | Supported (simulator / Catalyst) | Vardy Party does not ship Apple builds as a product today. |

Current agent package TFMs are `net10.0-*`. Confirm it restores and runs
against this repo's `net11.0-*` heads before merging a DevFlow PR.

**Cursor / Grok Bot:** `maui devflow mcp` is a stdio MCP server and can be
wired into Cursor manually:

```json
{
  "mcpServers": {
    "maui-devflow": {
      "command": "maui",
      "args": ["devflow", "mcp"]
    }
  }
}
```

Official docs: https://learn.microsoft.com/en-us/dotnet/maui/developer-tools/devflow/mcp-server
Put that in the user's Cursor MCP config (`~/.cursor/mcp.json` or the
Cursor settings UI), not in a #162 commit, unless Jon asks to add a project
file. `maui ai init` environments are **Claude, VsCode, CopilotCli, and
OpenCode only** — there is no `--env Cursor`. The MCP server talks to a
DevFlow agent on the same machine as the running app (typically the MSI).
Cloud/box agents cannot see that app through MCP; on the MSI they can also
use the CLI directly (`maui devflow list`, `maui devflow ui tree`,
`maui devflow ui screenshot`, `maui devflow logs`, `maui devflow network`)
against a Debug build that has the agent registered.

### Agent skills (`maui ai init`)

When the CLI is installed and Jon wants a preview:

```powershell
maui ai init --dry-run
# or, to force a documented environment without writing:
maui ai init --env VsCode --dry-run
```

`--dry-run` must not write, but it may still use the network. Apply nothing
(`--yes`) without approval. Do not dump generated skills, Copilot agents, or
`.maui/ai-assets.json` into this repo without a review. Official init
targets VS Code, Copilot CLI, Claude Code, and OpenCode — not Cursor. Useful
MAUI/DevFlow skills can later be installed into a Cursor skill path by hand
if the dry-run output is useful.

### Essentials.AI

`Microsoft.Maui.Essentials.AI` is on-device `Microsoft.Extensions.AI`
(Apple Intelligence chat, Apple NaturalLanguage embeddings; the NuGet 11.0
preview also documents Windows AI on Copilot+ hardware and Android Gemini
Nano, while some Learn pages still list Android/Windows as planned). It is
an **app feature**, not agent tooling. Do not add it unless there is a
product request for on-device AI. Vardy Party's shipped surfaces are
Windows, Android TV, and Linux, where support is limited or hardware-gated.

## Tests

Every `[Fact]` / `[Theory]` needs `// Arrange`, `// Act`, and `// Assert` in
that order. Create specimens with AutoFixture (`AutoMoqFixture.Create()`,
`Build`/`Create`). Use `_fixture.GetMock<T>()` — never `new Mock<T>()`.
Test data must be fictional: no real teams, leagues, competitions, or
streaming sites. See `.cursor/rules/csharp-tests.mdc`.
