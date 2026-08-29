# Agent playbook — close Client PR #74 and release v2.0.0 from main

**PLAYBOOK ONLY — DO NOT EXECUTE YET.**

- The human must **approve** PR #74 first.
- Agents must **not** merge the PR, bump versions, run Actions workflows, or push to `main` until the human pastes the copy-paste execute prompt at the bottom **after** approval.
- This document is instructions for a later run. Writing it is not permission to close the PR.

Status: nothing in here has been executed. No merge, no version bump.

## Goal

After a human approves it, merge [PR #74](https://github.com/Vardy-Party/Client/pull/74)
(`cursor/maui-avalonia-homepage-1e7d` → `main`) so that **the next Client release built from
`main` carries `ApplicationDisplayVersion` `2.0.0` on every platform** (Windows MSIX, Android
APK, iOS, macOS, Linux x64/arm64 snaps), and so the published GitHub Release is tagged
`2.0.0-b<N>` rather than another `1.7.116-b<N>`.

## Verified facts this playbook is built on

Checked against the repo at the time of writing — re-verify before executing, because the
numbers move.

| Fact | Where | Value now |
| --- | --- | --- |
| Display version on `main` | `Version.props` | `1.7.116` |
| Build counter on `main` | `Version.props` | `ApplicationVersion` `155` |
| Build counter on PR #74 branch | `Version.props` | `ApplicationVersion` `156` |
| Merge base | `git merge-base origin/main HEAD` | same content as `main` (155 / 1.7.116) |
| Latest published release | `gh release list` | `1.7.116-b155` |
| PR #74 state | `gh pr view 74` | OPEN, **draft**, `MERGEABLE`, `mergeStateStatus: BLOCKED`, `REVIEW_REQUIRED`, ~100 files |

Workflow behaviour that drives the ordering decision below:

- `.github/workflows/bump-display-version.yml` — `workflow_dispatch` only, checks out `main`,
  rewrites `Version.props`, opens branch `ci/bump-display-version-<display>-b<build>` and
  **squash-merges it itself** (main is protected, so it cannot push directly). Inputs:
  `version_bump_type` (major | minor | patch) and `bump_build_number` (**defaults to true** —
  also increments `ApplicationVersion` by 1). `major` from `1.7.116` gives `2.0.0`. It does
  **not** create a git tag, despite what `docs/VERSION_MANAGEMENT.md` claims.
- `.github/workflows/ci.yml` — the `resolve-flags` gate skips pushes to `main` whose head
  commit message starts with `ci: bump` or `ci: auto-increment`, and skips PRs authored by
  `github-actions[bot]`. So **the version-bump commit itself runs no CI and therefore
  produces no package**.
- `.github/workflows/increment-build-version.yml` — `workflow_run` on successful **CI on
  main**. Skips when the packaged commit's subject starts with `ci: bump`. Otherwise resolves
  `ApplicationVersion` as `max(Version.props on the commit, highest published -bN + 1)` and
  dispatches CD with `ci_run_id`, `application_version`, `package_sha`.
- `.github/workflows/cd.yml` — every packaging job is `workflow_dispatch`-only. It checks out
  `package_sha`, optionally stamps the passed `ApplicationVersion`, then reads
  **`ApplicationDisplayVersion` from `Version.props` of that commit** and names the release
  `VardyParty v<display>-b<build>` with tag `<display>-b<build>` (no leading `v`).
- `.github/workflows/release.yml` — triggered by `v*` tags only. CD's tags have no `v`
  prefix, so the normal main flow does **not** touch `release.yml`. Ignore it here.
- `.github/workflows/bump-version-on-pr.yml` ("ApplicationVersion on PR") — on every
  PR open/synchronize/reopen/ready_for_review against `main`, runs
  `scripts/sync-application-version.sh --write` and commits `ApplicationVersion` onto the PR
  head. The script picks `max(origin/main, highest released -bN, other open PR heads) + 1`
  unless the local value is already higher. It skips markdown-only / `Version.props`-only
  diffs and PRs titled `ci:`.

Out of scope: this is Client packaging. The Worker production-deploy rules from the `api`
repo do not apply — there is no Worker step in this release.

## Preconditions checklist

Do not start until all of these are true:

- [ ] **Human approval recorded on PR #74.** `reviewDecision` must not be `REVIEW_REQUIRED`.
- [ ] **PR #74 is out of draft.** It is a draft right now; `mergeStateStatus: BLOCKED` will
      not clear while it is.
- [ ] **CI green on the PR head.** `gh pr checks 74` — Build & Test plus Code Quality.
- [ ] **`mergeable: MERGEABLE`** and no `Version.props` conflict (see the sequence below).
- [ ] **Local working tree clean and synced.** Agents force-push this branch, so a human with
      a local copy must `git fetch origin cursor/maui-avalonia-homepage-1e7d` and
      `git reset --hard origin/cursor/maui-avalonia-homepage-1e7d` rather than pull/merge.
      Stash or commit anything local first — `git status --porcelain` should be empty.
- [ ] **No other product PR is mid-merge into `main`.** Two merges racing share the
      `increment-build-main` concurrency group and the release tag; land this one alone.
- [ ] **A human is available to click Actions buttons.** Both the merge and the
      `workflow_dispatch` bump generally need write rights an agent does not have.

## Recommended sequence: bump the display version BEFORE merging PR #74

**Do the `major` bump on `main` first, then merge PR #74.** Uncheck `bump_build_number`.

Why this is the safe order:

1. CD packages the display version found in `Version.props` **on the commit being packaged**.
   If `main` is already `2.0.0` when PR #74 lands, the merge commit is `2.0.0`, and the CI →
   Increment Build Version → CD chain that fires on that merge publishes `2.0.0-b156`. That
   is the guarantee the goal asks for.
2. If you merge first, the merge commit still says `1.7.116`, so CD immediately publishes
   `1.7.116-b156` — a 1.7.x release that actually contains the v2 homepage work. That release
   cannot be un-published cleanly.
3. Bumping afterwards does **not** fix that, because the bump commit is skipped by CI
   (`ci: bump` guard) and by Increment Build Version (same guard). `2.0.0` would then sit on
   `main` unpackaged until either the *next* product merge or a manual CD dispatch. Re-running
   CI on `main` by hand does not help: Increment Build Version still sees a `ci: bump` tip and
   skips.
4. Unchecking `bump_build_number` is what avoids the `Version.props` fight. The PR branch
   changed only the `ApplicationVersion` line (155 → 156); the bump then changes only the
   `ApplicationDisplayVersion` line. Different, non-adjacent lines merge cleanly, and if
   branch protection makes the human update the branch, `sync-application-version.sh` sees
   `main` still at 155 and leaves the branch at 156. Leave `bump_build_number` ticked and
   `main` moves to 156 too; any later push to the PR then re-syncs the branch to 157 and you
   get a genuine three-way conflict on that one line.
5. Losing nothing: `ApplicationVersion` 156 is already on the PR branch (that is exactly what
   the "ApplicationVersion on PR" workflow is for), and Increment Build Version would bump to
   `released_max + 1` anyway if it were behind.

The window to respect: between the bump landing and PR #74 merging, `main` is `2.0.0`, so any
*other* merge in that window ships `2.0.0` with unrelated content. Keep the gap short and
don't land anything else.

## Ordered steps

### Step 1 — Final verification before merge (Agent)

```bash
cd /agent/repos/client
git fetch origin main cursor/maui-avalonia-homepage-1e7d
gh pr view 74 --json state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid
gh pr checks 74
git show origin/main:Version.props | grep -E 'ApplicationVersion|ApplicationDisplayVersion'
git show origin/cursor/maui-avalonia-homepage-1e7d:Version.props | grep -E 'ApplicationVersion|ApplicationDisplayVersion'
gh release list --limit 5
```

Report: PR checks status, both `Version.props` pairs, highest published `-bN`. Stop and ask a
human if checks are red, the PR is still a draft, review is still required, or the PR branch
has touched `ApplicationDisplayVersion` (it must not — see Pitfalls).

### Step 2 — Bump the display version on main to 2.0.0 (Human, in the Actions UI)

This is the critical step and it comes **before** the merge.

1. Open <https://github.com/Vardy-Party/Client/actions/workflows/bump-display-version.yml>.
2. Click **Run workflow**.
3. **Use workflow from**: `Branch: main` (the workflow checks out `main` regardless, but keep
   the dispatch on `main`).
4. **ApplicationDisplayVersion bump type**: select **`major`** — `1.7.116` → **`2.0.0`**.
5. **Also increment ApplicationVersion by 1**: **untick** it (see reason 4 above). Leave
   `ApplicationVersion` on `main` at 155.
6. Click the green **Run workflow** button.
7. Watch the run. Expect the notices
   `ApplicationDisplayVersion 1.7.116 → 2.0.0 (major)` and
   `ApplicationDisplayVersion 2.0.0 (build 155) is on main`, and a squash-merged PR titled
   `ci: bump ApplicationVersion to 155 and ApplicationDisplayVersion to 2.0.0`.
8. If the run fails at the PR step, it tells you which permission is missing: either
   Settings → Actions → General → *Allow GitHub Actions to create and approve pull requests*,
   or branch protection blocking `github-actions[bot]` from squash-merging. Fix that, or merge
   the bot's version-bump PR by hand — do not hand-edit `Version.props` on `main`.

Confirm before moving on:

```bash
git fetch origin main && git show origin/main:Version.props | grep ApplicationDisplayVersion
# expect: <ApplicationDisplayVersion>2.0.0</ApplicationDisplayVersion>
```

No CI, no CD and no release should fire from this bump. That is expected — the `ci: bump`
guards suppress them.

### Step 3 — Merge PR #74 (Human; Agent only if it truly has write rights)

`main` is protected and PR #74 needs an approving review, so in practice **the human merges**.
Agents in this repo normally cannot: `gh` is read-only for cloud agents and `ManagePullRequest`
cannot satisfy required reviews. An agent should prepare and verify, then hand over.

If branch protection requires the branch to be up to date with `main`, update it first — the
2.0.0 line will merge in cleanly:

```bash
gh pr view 74 --json mergeStateStatus       # BEHIND means update required
# Human, locally: bring main's 2.0.0 into the PR branch
git checkout cursor/maui-avalonia-homepage-1e7d
git pull origin cursor/maui-avalonia-homepage-1e7d
git merge origin/main
git push origin cursor/maui-avalonia-homepage-1e7d
```

That push re-triggers "ApplicationVersion on PR"; with `bump_build_number` unticked in Step 2
it should log `ApplicationVersion already 156` (or move it to `156` if something else landed).

Then merge, matching the repo's existing history style (squash, PR title as subject):

```bash
gh pr ready 74                       # only if still a draft
gh pr merge 74 --squash --delete-branch
```

Or in the UI: PR #74 → **Squash and merge** → keep the title
`One MAUI XAML homepage for every platform, drawn by Avalonia on Linux` → confirm. Do **not**
prefix the squash subject with `ci:` — that would make CI skip the merge and no release would
be built at all.

### Step 4 — Watch the automation (Agent)

In order, on `main`:

1. **CI - Build & Test** on the merge commit — must be green.
2. **Increment Build Version** — fires on CI success. Expect the notice
   `Packaging ApplicationVersion 156 (commit 156, released max 155)` and
   `Dispatched CD for ApplicationVersion 156`. If it logs a "Skipping — triggering commit is
   already a version commit" notice, the squash subject was wrong (see Step 3).
3. **CD - Package & Release** — one dispatched run, jobs for Windows, Android, iOS, macOS,
   Linux x64, Linux arm64. Each `Extract app version metadata` step must print
   `Using app version: 2.0.0 (build 156)`.
4. **Release** `VardyParty v2.0.0-b156`, tag `2.0.0-b156`, with the platform assets attached.
   `release.yml` is *not* involved (it only listens for `v*` tags).

```bash
gh run list --branch main --limit 10
gh run view <run-id> --log-failed        # for any red job
gh release view 2.0.0-b156 --json tagName,name,assets --jq '{tagName,name,assets:[.assets[].name]}'
```

### Step 5 — Post-merge verification (Agent, then Human for local APK)

```bash
git fetch origin main
git show origin/main:Version.props | grep -E 'ApplicationVersion|ApplicationDisplayVersion'
# expect ApplicationDisplayVersion 2.0.0 and ApplicationVersion 156
gh release list --limit 3                # expect 2.0.0-b156 as Latest
```

- Every platform picks 2.0.0 up automatically: both `VardyParty/VardyParty.csproj` and the
  Linux/Avalonia head import the same root `Version.props`, and CD reads that one file per
  job. There is no per-platform version to edit.
- Asset names should read `VardyParty-windows-v2.0.0-b156.msix`,
  `VardyParty-android-v2.0.0-b156.apk`, and so on.
- Local Android APK (Human, Windows/pwsh):

```powershell
git fetch origin main
git checkout main
git reset --hard origin/main     # agents force-push; reset rather than pull
pwsh ./package-android.ps1       # -Mode all for the fat/store APK
```

  The APK version comes from the same `Version.props`, so it should report `2.0.0`. If the
  tree is dirty, stash first — the script patches and then git-restores
  `VardyParty/appsettings.json`.

### Step 6 — Rollback / recovery

- **Bump landed but PR #74 has to be abandoned.** `main` sits at `2.0.0` with no 2.0.0
  release. Nothing is broken, but the next unrelated merge will ship as 2.0.0. Either accept
  that or run Bump Display Version again — it can only go forward, so there is no way back to
  1.7.x through the workflow. A downgrade would need a reviewed PR editing `Version.props`
  by hand, which is a deliberate, human decision.
- **Wrong bump type chosen** (e.g. `minor` → `1.8.0` instead of `2.0.0`). Do not hand-edit.
  Re-run the workflow with `major`: `1.8.0` → `2.0.0`. Check the arithmetic before merging —
  from `1.8.0` a `major` bump also gives `2.0.0`, but from `2.0.0` it gives `3.0.0`.
- **Bumped twice by accident** (`3.0.0`). There is no downwards path in the workflow. Stop and
  get a human decision: either ship 3.0.0 or land a reviewed `Version.props` PR.
- **Merged before bumping** (the mistake this playbook exists to prevent). A `1.7.116-b156`
  release is now published. Then: bump `major` to `2.0.0` (Step 2), and because the bump
  commit is CI-skipped you must package 2.0.0 by hand — Actions → **CD - Package & Release**
  → Run workflow on `main` with `ci_run_id` = the successful main CI run for the PR #74 merge,
  `produce_release_assets` = true, `application_version` = `157`, `package_sha` = the current
  `main` tip (the bump commit, so `Version.props` reads 2.0.0). Consider marking the
  `1.7.116-b156` release as a pre-release or deleting it so users don't take it as latest.
- **CD failed on one platform only.** Re-dispatch CD with the same `ci_run_id`,
  `package_sha` and `application_version`; asset upload uses `--clobber`, so re-running is
  safe and the tag is reused.

## Pitfalls

- **Feature-branch `Version.props` vs the main bump PR.** The display bump must happen through
  the Bump Display Version workflow on `main`, never by editing `ApplicationDisplayVersion` on
  the PR #74 branch. A branch-side display edit collides with the bot's bump PR on the same
  line and turns a clean merge into a conflict.
- **Don't ship 1.7.x after the merge.** Forgetting the major bump is the default failure mode:
  the merge alone publishes `1.7.116-b156` automatically, within minutes, with no further
  clicks. Bump first.
- **`ApplicationVersion` is not the display version.** `ApplicationVersion` (155/156) is an
  integer build counter, set on the PR branch by the "ApplicationVersion on PR" workflow and
  finalised by Increment Build Version. `ApplicationDisplayVersion` (`1.7.116` → `2.0.0`) is
  the user-facing semver. "Bumping the version" is ambiguous — always say which one.
- **Leaving `bump_build_number` ticked** moves `main`'s counter and sets up a one-line
  three-way conflict on the next push to the PR. Untick it.
- **A `ci:`-prefixed squash subject** on the product merge makes CI, Increment Build Version
  and therefore CD skip the merge entirely — no release at all.
- **`docs/VERSION_MANAGEMENT.md` is stale.** It still describes auto-increment on `main`,
  git tags created by the bump workflow, and versions `1.7.2`/`35`. Trust the workflow YAML
  and `Version.props`, not that document.
- **Tags have no `v` prefix.** CD creates `2.0.0-b156`; the release *name* is
  `VardyParty v2.0.0-b156`. Searching for a `v2.0.0-b156` tag will come up empty, and
  `release.yml` (which watches `v*`) is not part of this flow.
- **Agent force-pushes.** A human with a local checkout of the PR branch must
  `git fetch` + `git reset --hard`, not `git pull`.

## Copy-paste prompt for a future agent

Paste this after PR #74 has been approved and is out of draft:

```text
Execute docs/agent-playbook-merge-client-pr-v2.md in /agent/repos/client
(github.com/Vardy-Party/Client) to release v2.0.0.

Do this:
1. Run Step 1 (final verification) and report PR #74's checks, review state, draft state,
   both Version.props values (main and the PR branch) and the highest published -bN release.
2. Confirm the recommended order still holds: Bump Display Version (major, bump_build_number
   UNTICKED) on main FIRST, then squash-merge PR #74. Tell me the exact Actions clicks for
   Step 2 and wait — I will run the bump and the merge myself; you do not have write rights.
3. After I confirm the bump, verify main's Version.props reads ApplicationDisplayVersion
   2.0.0 before I merge.
4. After I merge, run Steps 4 and 5: watch CI - Build & Test on main, then Increment Build
   Version (expect "Packaging ApplicationVersion 156"), then CD - Package & Release (every
   job must print "Using app version: 2.0.0 (build 156)"), then confirm release 2.0.0-b156
   exists with all platform assets. Paste failing logs if anything is red.
5. Do not edit Version.props by hand, do not bump anything yourself, do not push to main, and
   do not touch any Cloudflare Worker — this is Client packaging only.
```
