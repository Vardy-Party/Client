# Cloud Agent environment — VardyParty client

This folder configures how the client is provisioned inside a Cursor Cloud Agent
(part of the `m3u8-resolver` + `api` + `client` multi-repo workspace environment).

- `install.sh` — restores and builds the **Linux (Avalonia) head** and all shared
  libraries in Release. The MAUI heads (Android/iOS/macOS/Windows) are not built:
  they require extra workloads and cannot run in a headless Linux VM.

## Required secret: `NUGET_GITHUB_TOKEN`

The client's `VardyParty.Streaming` project references the **`VardyParty.LocalService.Client`**
NuGet package, which is published to the private **GitHub Packages** feed
`https://nuget.pkg.github.com/<ORG>/index.json` (not nuget.org), where `<ORG>` is the
GitHub organization that owns these repos (`install.sh` derives it from the git remote).
Restoring it requires a GitHub token with the **`read:packages`** scope for that org.

Set these in the Cloud Agent **Secrets** panel
(https://cursor.com/dashboard/cloud-agents):

| Secret | Required | Notes |
| --- | --- | --- |
| `NUGET_GITHUB_TOKEN` | yes | GitHub PAT with `read:packages`. Add as a Runtime Secret. |
| `NUGET_GITHUB_USERNAME` | no | Feed username. GitHub Packages validates the token, not the username, so any value works; defaults to a generic placeholder. |

`install.sh` writes these into a throwaway NuGet config (removed on exit), so the
token is never committed or captured in an environment snapshot. If the secret is
absent, `install.sh` skips the client build with a warning instead of failing.

## ⚠️ Token expires every ~90 days — rotate it

`NUGET_GITHUB_TOKEN` is a GitHub Personal Access Token that **expires roughly 90
days after it is created**. Once it lapses, `dotnet restore` fails against
`nuget.pkg.github.com` with `401 Unauthorized` / `403 Forbidden` and the client
build stops working.

To rotate:

1. Create a new GitHub PAT with the **`read:packages`** scope for the GitHub org
   that owns these repos (GitHub → Settings → Developer settings → Personal access tokens).
2. Update the **`NUGET_GITHUB_TOKEN`** value in the Cloud Agent Secrets panel.
3. New Cloud Agents pick up the new value automatically; no code change needed.

> Tip: set a calendar reminder a few days before the 90-day mark, or (if org policy
> allows) issue the PAT with a longer/no expiry to reduce rotation churn.
