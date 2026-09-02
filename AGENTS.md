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
