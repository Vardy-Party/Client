#!/usr/bin/env bash
# WSL body for scripts/launch-linux-app.ps1.
# Strip CR so this file and merge-appsettings-secrets.sh still run if
# Windows checks them out as CRLF.
set -euo pipefail

export DISPLAY="${DISPLAY:-:0}"
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/mnt/wslg/runtime-dir}"
export USER_SECRETS_ID="${USER_SECRETS_ID:-543d9e88-b60c-4397-bc9d-c4614b8b1dcb}"

APPSETTINGS="VardyParty.Linux/appsettings.json"
LOG="$(mktemp /tmp/vardyparty-linux-launch.XXXXXX.log)"

restore_appsettings() {
  git restore -- "$APPSETTINGS" 2>/dev/null || true
}
trap restore_appsettings EXIT

# bash -s: stdin is the script; -- args become $1 (process substitution drops them).
tr -d '\r' < scripts/merge-appsettings-secrets.sh | bash -s -- "$APPSETTINGS"

{
  "$HOME/.dotnet/dotnet" restore VardyParty.Linux/VardyParty.Linux.csproj \
    --ignore-failed-sources -p:HomeUiTargetFrameworks=net11.0 \
  && "$HOME/.dotnet/dotnet" run --project VardyParty.Linux/VardyParty.Linux.csproj \
    -c Release --no-restore -p:HomeUiTargetFrameworks=net11.0
} 2>&1 | tee "$LOG"
RC="${PIPESTATUS[0]}"

if [[ "$RC" -ne 0 ]] && grep -q NETSDK1147 "$LOG"; then
  echo
  echo "NETSDK1147: the android workload leaked into the Linux build graph."
  echo "Remedy: dotnet workload install android"
fi
rm -f "$LOG"
exit "$RC"
