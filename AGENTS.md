# Agent notes — VardyParty Client

## GitHub identity for this repo

Use **`GH_TOKEN`** as the `gh` identity for pull requests and other GitHub API
work on this repository. Do not use the default Cursor `gh` account; it is not
a collaborator here.

| Secret | Purpose | Scopes |
| --- | --- | --- |
| `GH_TOKEN` | PAT for `gh` / PRs / repo API | `repo`, `read:org` |
| `NUGET_GITHUB_TOKEN` | GitHub Packages restore only | `read:packages` |

`NUGET_GITHUB_TOKEN` cannot open pull requests. Prefer `GH_TOKEN` whenever `gh`
needs repo access. The Packages username secret (`NUGET_GITHUB_USERNAME`) is
the same GitHub login `GH_TOKEN` must belong to.

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
