# Agent playbook — scrub appsettings secrets (Client)

**Repo:** `github.com/Vardy-Party/Client`  
**Local:** `C:\Users\jonbr\source\repos\VardyParty-Client`

Paste for a local agent:

```text
Read and execute docs/agent-playbook-scrub-appsettings-secrets.md in this repo.
Report progress after each phase. Do not print secret values.
```

---

## Problem

Live Auth0 + API values were committed in:

- `VardyParty/appsettings.json`
- `VardyParty.Desktop/appsettings.json`

They must be **templates only** in git. CD / local scripts inject secrets at build time.

Open tip scrub: **PR #79** (`cursor/scrub-appsettings-secrets-1e7d`). Tip scrub ≠ history scrub.

## Rules

- Never print Domain / ClientId / Audience / API URLs / role claim strings.
- Never commit filled secrets into appsettings.
- No Worker / Api production deploy.
- **Force-push history only after the human types:** `rewrite history now`
- Squash subjects must not start with `ci:`.

## Phase 1 — Clean tip + Linux enrichment

```powershell
cd C:\Users\jonbr\source\repos\VardyParty-Client
git fetch origin
git status --porcelain   # clean or stash first
gh pr view 79
git checkout cursor/scrub-appsettings-secrets-1e7d
git reset --hard origin/cursor/scrub-appsettings-secrets-1e7d
```

### 1a. Templates

Both appsettings files on the branch:

| Keep | Empty string |
|------|----------------|
| Logging, GamesApi, BbcFixtures, StreamHealth timeouts | `Auth0.Domain`, `ClientId`, `Audience` |
| `Scope`, `CallbackScheme`, redirect URIs, `TokenLeewaySeconds` | `RequiredRoleClaimType`, `RequiredRole` |
| `Api.HeadlessBaseUrl-Local` (`https://127.0.0.1:8787/`) | `Api.HeadlessBaseUrl`, `HeadlessBaseUrl-Preview` |

No UTF-8 BOM. `IgnoreSslCertificateErrors` default `false` on MAUI if present.

### 1b. Linux / Desktop enrichment (required before merge)

If missing on the branch, implement and push:

1. `scripts/merge-appsettings-secrets.sh` — merge from user-secrets **or** env (`AUTH0_*`, `API_HEADLESSBASEURL`); fail if critical fields still empty.
2. `scripts/patch-appsettings.ps1` — Windows/Linux pwsh user-secrets merge; `patch-appsettings-android.ps1` thin wrapper.
3. `VardyParty.Desktop`: same `UserSecretsId` as MAUI; MSBuild target `PatchAppSettingsForLocalDesktop` when `-p:PatchAppSettings=true` (pwsh on Windows, bash script on Linux).
4. `scripts/launch-linux-app.cmd` — run merge script before build/run; `git restore` template on exit (`trap`).
5. `.github/workflows/cd.yml` Linux jobs + `.github/workflows/release.yml` `build-linux-release` — call `merge-appsettings-secrets.sh` with Actions secrets as env (not only inline jq).
6. Short note in `docs/LINUX_SUPPORT.md` (secrets table).

### 1c. Land tip

- CI green on #79 → squash-merge to `main` (human clicks if needed).
- Verify tip: Auth0 Domain/ClientId/Audience are empty strings (report booleans only).

## Phase 2 — Rotate (human; agent guides)

Before any history rewrite:

1. Auth0: rotate/reissue native app credentials (or new app, retire old).
2. Update GitHub Actions secrets (`AUTH0_*`, `API_HEADLESSBASEURL`, …).
3. Update local `dotnet user-secrets` (same UserSecretsId on MAUI + Desktop).
4. Confirm a CD/package path still injects the new values.

**Stop.** Wait for human: `rewrite history now`.

## Phase 3 — Purge git history

Only after that phrase:

```powershell
cd <parent>
git clone --mirror https://github.com/Vardy-Party/Client.git Client-mirror.git
cd Client-mirror.git
# install git-filter-repo if needed
```

Recommended approach:

1. Remove both appsettings paths from **all** history (`git filter-repo --path … --path … --invert-paths`, or BFG `--delete-files appsettings.json`).
2. `git reflog expire --expire=now --all && git gc --prune=now --aggressive`
3. After human confirm: `git push --force --all origin` and `git push --force --tags origin`
4. On rewritten `main`, restore the **template** appsettings in a normal commit if filter deleted them entirely (copy from scrubbed tip).
5. Verify GitHub search no longer finds the old ClientId (do not print it).
6. Tell every developer/agent to **re-clone**. Recreate open PRs. Note forks/Actions logs may still hold old blobs — GitHub Support / [removing sensitive data](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository) if needed.

## Phase 4 — Report

- #79 merge SHA; enrichment present? (yes/no)
- Tip templates empty? (yes/no per field group — no values)
- Rotation done? History rewrite done?
- Remaining risk (forks, artifacts, open PRs)
