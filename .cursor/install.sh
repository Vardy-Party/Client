#!/usr/bin/env bash
# Cloud Agent install for the VardyParty client.
#
# Restores + builds the Linux (Avalonia) head, which depends on the private
# GitHub Packages NuGet feed (VardyParty.LocalService.Client). Requires the
# NUGET_GITHUB_TOKEN secret (read:packages). Self-locating and idempotent.
#
# ┌─────────────────────────────────────────────────────────────────────────┐
# │ TOKEN EXPIRY: NUGET_GITHUB_TOKEN is a GitHub PAT that expires ~90 days   │
# │ after creation. When it lapses, `dotnet restore` fails with 401/403 from │
# │ nuget.pkg.github.com. Rotate it: create a new PAT with `read:packages`   │
# │ for the GitHub org that owns these repos, and update NUGET_GITHUB_TOKEN  │
# │ in the Cloud Agent Secrets panel. See .cursor/README.md.                 │
# └─────────────────────────────────────────────────────────────────────────┘
#
# The MAUI heads (Android/iOS/macOS/Windows) are NOT built here: they need
# extra workloads and cannot run in a headless Linux VM. The Linux head plus
# all shared libraries compile, which validates the client codebase.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# GitHub Packages validates the token, not the username, so any non-empty
# username works; NUGET_GITHUB_USERNAME can override the placeholder if needed.
TOKEN="${NUGET_GITHUB_TOKEN:-}"
USERNAME="${NUGET_GITHUB_USERNAME:-x-access-token}"
PROJECT="VardyParty.Linux/VardyParty.Linux.csproj"

# Derive the GitHub org from this repo's origin remote so the feed host is not
# hardcoded (GitHub package feeds are case-insensitive on the org segment).
ORG="${NUGET_GITHUB_ORG:-$(git -C "$REPO_ROOT" config --get remote.origin.url 2>/dev/null | sed -E 's#.*[/:]([^/]+)/[^/]+$#\1#; s#\.git$##')}"
FEED="https://nuget.pkg.github.com/${ORG}/index.json"

if [[ -z "$TOKEN" ]]; then
  echo "!! NUGET_GITHUB_TOKEN not set; skipping client restore/build." >&2
  echo "!! Add it (read:packages) in the Cloud Agent Secrets panel; the client needs" >&2
  echo "!! it to restore VardyParty.LocalService.Client from GitHub Packages." >&2
  exit 0
fi

# Write NuGet credentials to a throwaway config outside the repo so the token is
# never committed or captured in a snapshot; always remove it on exit.
CONFIG="$(mktemp "${TMPDIR:-/tmp}/vardyparty-nuget.XXXXXX.config")"
cleanup() { rm -f "$CONFIG"; }
trap cleanup EXIT
chmod 600 "$CONFIG"
cat > "$CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github-vardyparty" value="$FEED" />
  </packageSources>
  <packageSourceCredentials>
    <github-vardyparty>
      <add key="Username" value="$USERNAME" />
      <add key="ClearTextPassword" value="$TOKEN" />
    </github-vardyparty>
  </packageSourceCredentials>
</configuration>
EOF

echo "==> Restoring client Linux head from GitHub Packages"
dotnet restore "$PROJECT" --configfile "$CONFIG"

echo "==> Building client Linux head (Release)"
dotnet build "$PROJECT" -c Release --no-restore

echo "==> Client install complete."
