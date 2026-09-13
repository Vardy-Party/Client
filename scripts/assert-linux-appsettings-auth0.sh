#!/usr/bin/env bash
# Verify a Linux/Desktop build output has non-empty Auth0 ClientId/Domain
# in appsettings.json. Does not print secret values — only EMPTY/NON_EMPTY.
#
# Usage:
#   bash scripts/assert-linux-appsettings-auth0.sh VardyParty.Linux/bin/Release/net11.0/appsettings.json
set -euo pipefail

APPSETTINGS_PATH="${1:-}"
if [[ -z "$APPSETTINGS_PATH" ]]; then
  echo "Usage: $0 <appsettings.json>" >&2
  exit 2
fi

if [[ ! -f "$APPSETTINGS_PATH" ]]; then
  echo "appsettings.json not found: $APPSETTINGS_PATH" >&2
  exit 1
fi

# Prefer python3 (always available on WSL/Ubuntu); fall back to a tiny jq-less parse.
eval "$(
  python3 - "$APPSETTINGS_PATH" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    data = json.load(f)
auth0 = data.get("Auth0") or {}
client = str(auth0.get("ClientId") or "").strip()
domain = str(auth0.get("Domain") or "").strip()
print(f"CLIENT_STATE={'NON_EMPTY' if client else 'EMPTY'}")
print(f"DOMAIN_STATE={'NON_EMPTY' if domain else 'EMPTY'}")
PY
)"

echo "[LINUX CHECK] $APPSETTINGS_PATH Auth0.ClientId=$CLIENT_STATE Auth0.Domain=$DOMAIN_STATE"

if [[ "$CLIENT_STATE" != "NON_EMPTY" || "$DOMAIN_STATE" != "NON_EMPTY" ]]; then
  cat >&2 <<'EOF'
Linux build output has empty Auth0 ClientId/Domain in appsettings.json.
Merge user-secrets before build (scripts/launch-linux-app.sh or -p:PatchAppSettings=true)
and do not git restore VardyParty.Linux/appsettings.json until the output exists.
EOF
  exit 1
fi
