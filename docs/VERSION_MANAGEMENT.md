# Version management

Single source of truth: root [`Version.props`](../Version.props).

| Property | Meaning | Who changes it |
|----------|---------|----------------|
| `ApplicationDisplayVersion` | User-facing semver (`Major.Minor.Patch`) | Human via **Bump Display Version** workflow |
| `ApplicationVersion` | Integer build counter | **ApplicationVersion on PR** (feature branches) and **Increment Build Version** (after successful CI on `main`) |

Every platform package (APK, MSIX, snaps, etc.) reads the same file. There is
nothing per-platform to bump.

## Current model (trust workflows, not old prose)

```mermaid
flowchart TD
  PR["Feature PR<br/>ApplicationVersion on PR bumps counter on the branch"]
  Merge["Merge to main"]
  CI["CI - Build and Test"]
  Inc["Increment Build Version<br/>resolves packaging ApplicationVersion"]
  CD["CD - Package and Release<br/>reads ApplicationDisplayVersion from Version.props"]
  Bump["Bump Display Version<br/>workflow_dispatch on main<br/>major / minor / patch"]

  PR --> Merge --> CI --> Inc --> CD
  Bump -->|"rewrites Version.props on main<br/>ci: bump commit skips push CI"| MainProps["main Version.props"]
  Bump -->|"dispatch-cd-for-release.sh"| CD
  MainProps -.->|"next product merge"| CD
```

### Display version (semver)

1. GitHub → **Actions** → **Bump Display Version**
2. Run on **`main`**
3. Choose `major` / `minor` / `patch`  
   Example: `1.7.116` + **major** → **`2.0.0`**
4. Workflow opens a short PR and squash-merges it (main is protected).
5. Push CI skips that `ci: bump` commit (no product CI), but the workflow then
   runs `scripts/dispatch-cd-for-release.sh`, which dispatches **CD - Package &
   Release** for the new `main` tip. CD builds every platform from that commit's
   source and publishes `{display}-b{build}` (e.g. `3.6.1-b262`).

To ship a product release as **2.0.0**, put `2.0.0` on `main` **before** the
product merge that should package, or follow
[agent-playbook-merge-client-pr-v2.md](agent-playbook-merge-client-pr-v2.md).

### Build counter

- On PRs: **ApplicationVersion on PR** keeps the branch counter ahead of
  published `-bN` releases. Its Version.props commit is pushed with
  `GITHUB_TOKEN`, which starts no workflows, so it does not cancel or restart
  the product CI already running for that synchronize. The message carries no
  skip-CI token: a squash merge copies it into main's commit and would skip push
  CI on `main`.
- On `main` after green CI: **Increment Build Version** dispatches CD with the
  packaging `ApplicationVersion`.
- Release **name**: `VardyParty v{display}-b{build}`  
  Release **tag**: `{display}-b{build}` (no leading `v` — `release.yml` on `v*`
  tags is a separate path).

### Local check

```bash
grep ApplicationDisplayVersion Version.props
grep ApplicationVersion Version.props
```

## Do not

- Hand-edit `ApplicationDisplayVersion` on a product feature branch (conflicts
  with the bump workflow on the same line).
- Confuse `ApplicationVersion` (155, 156, …) with the display semver.
- Dispatch CD by hand after a bump: Bump Display Version already dispatches it
  (CD creates the `{display}-b{build}` tag and release).
- Put a skip-CI token in any commit or squash message, or leave one in a squash
  body built from branch commits. It stops push CI on `main` (no artifacts) and
  broke CD for 3.6.0-b261. See [CI_SKIP_TOKENS.md](CI_SKIP_TOKENS.md).
