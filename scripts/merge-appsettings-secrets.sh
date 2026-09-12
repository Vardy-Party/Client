#!/usr/bin/env bash
# Merge Auth0 / Api secrets into an appsettings.json template.
#
# Sources (first match wins):
#   1) User-secrets JSON when USER_SECRETS_ID is set
#      (~/.microsoft/usersecrets/<id>/secrets.json on Linux/macOS,
#       %APPDATA%\Microsoft\UserSecrets\<id>\secrets.json on Windows via WSL path)
#   2) Environment variables (CI / CD): AUTH0_DOMAIN, AUTH0_CLIENTID, AUTH0_AUDIENCE,
#      AUTH0_SCOPE, AUTH0_CALLBACKSCHEME, AUTH0_REDIRECTURI, AUTH0_POSTLOGOUTREDIRECTURI,
#      AUTH0_TOKENLEEAWAYSECONDS, AUTH0_REQUIREDROLECLAIMTYPE, AUTH0_REQUIREDROLE,
#      API_HEADLESSBASEURL
#
# Usage:
#   scripts/merge-appsettings-secrets.sh VardyParty.Linux/appsettings.json
#   USER_SECRETS_ID=... scripts/merge-appsettings-secrets.sh path/to/appsettings.json
#
# Requires: python3 (stdlib only).
set -euo pipefail

APPSETTINGS_PATH="${1:-}"
if [[ -z "$APPSETTINGS_PATH" ]]; then
  echo "Usage: $0 <appsettings.json>" >&2
  exit 2
fi

if [[ ! -f "$APPSETTINGS_PATH" ]]; then
  echo "ERROR: appsettings not found: $APPSETTINGS_PATH" >&2
  exit 1
fi

resolve_user_secrets_file() {
  local id="${USER_SECRETS_ID:-}"
  [[ -z "$id" ]] && return 1

  local candidates=()
  if [[ -n "${HOME:-}" ]]; then
    candidates+=("$HOME/.microsoft/usersecrets/$id/secrets.json")
  fi
  if [[ -n "${APPDATA:-}" ]]; then
    candidates+=("$APPDATA/Microsoft/UserSecrets/$id/secrets.json")
  fi
  # WSL reading a Windows user-secrets store
  if command -v wslpath >/dev/null 2>&1 && [[ -n "${USERPROFILE:-}" || -d /mnt/c/Users ]]; then
    local win_appdata="${APPDATA:-}"
    if [[ -z "$win_appdata" && -n "${USERPROFILE:-}" ]]; then
      win_appdata="$USERPROFILE/AppData/Roaming"
    fi
    if [[ -n "$win_appdata" ]]; then
      local wsl_appdata
      wsl_appdata="$(wslpath -u "$win_appdata" 2>/dev/null || true)"
      if [[ -n "$wsl_appdata" ]]; then
        candidates+=("$wsl_appdata/Microsoft/UserSecrets/$id/secrets.json")
      fi
    fi
  fi

  local c
  for c in "${candidates[@]}"; do
    if [[ -f "$c" ]]; then
      printf '%s\n' "$c"
      return 0
    fi
  done
  return 1
}

SECRETS_FILE=""
if SECRETS_FILE="$(resolve_user_secrets_file)"; then
  echo "[appsettings] Merging user-secrets from $SECRETS_FILE into $APPSETTINGS_PATH"
  SOURCE_MODE=usersecrets
elif [[ -n "${AUTH0_DOMAIN:-}${AUTH0_CLIENTID:-}${API_HEADLESSBASEURL:-}" ]]; then
  echo "[appsettings] Merging CI/CD environment secrets into $APPSETTINGS_PATH"
  SOURCE_MODE=env
  SECRETS_FILE=""
else
  echo "ERROR: no secrets source for $APPSETTINGS_PATH" >&2
  echo "  Set USER_SECRETS_ID (and populate user-secrets), or export AUTH0_* / API_HEADLESSBASEURL." >&2
  exit 1
fi

export APPSETTINGS_PATH SECRETS_FILE SOURCE_MODE
python3 <<'PY'
import json, os, sys
from pathlib import Path

path = Path(os.environ["APPSETTINGS_PATH"])
mode = os.environ["SOURCE_MODE"]

raw = path.read_text(encoding="utf-8-sig")
data = json.loads(raw) if raw.strip() else {}
auth0 = data.setdefault("Auth0", {})
api = data.setdefault("Api", {})

def set_auth(key, value):
    if value is None:
        return
    if isinstance(value, str) and value == "" and key != "Scope":
        # Allow explicit empty from env only when key present; skip blanks from user-secrets misses
        return
    auth0[key] = value

def set_api(key, value):
    if value is None or value == "":
        return
    api[key] = value

if mode == "usersecrets":
    secrets = json.loads(Path(os.environ["SECRETS_FILE"]).read_text(encoding="utf-8-sig"))
    for k, v in secrets.items():
        if k.startswith("Auth0:"):
            leaf = k.split(":", 1)[1]
            if leaf == "TokenLeewaySeconds":
                auth0[leaf] = int(v)
            else:
                auth0[leaf] = v
        elif k.startswith("Api:"):
            api[k.split(":", 1)[1]] = v
else:
    env = os.environ
    mapping = {
        "Domain": "AUTH0_DOMAIN",
        "ClientId": "AUTH0_CLIENTID",
        "Audience": "AUTH0_AUDIENCE",
        "Scope": "AUTH0_SCOPE",
        "CallbackScheme": "AUTH0_CALLBACKSCHEME",
        "RedirectUri": "AUTH0_REDIRECTURI",
        "PostLogoutRedirectUri": "AUTH0_POSTLOGOUTREDIRECTURI",
        "RequiredRoleClaimType": "AUTH0_REQUIREDROLECLAIMTYPE",
        "RequiredRole": "AUTH0_REQUIREDROLE",
    }
    for leaf, env_name in mapping.items():
        if env_name in env and env[env_name] != "":
            auth0[leaf] = env[env_name]
    if "AUTH0_TOKENLEEAWAYSECONDS" in env and env["AUTH0_TOKENLEEAWAYSECONDS"] != "":
        auth0["TokenLeewaySeconds"] = int(env["AUTH0_TOKENLEEAWAYSECONDS"])
    if env.get("API_HEADLESSBASEURL"):
        api["HeadlessBaseUrl"] = env["API_HEADLESSBASEURL"]

data.pop("AllowUserSecrets", None)

# Validate we did not leave the critical empties when not sample-data
if not os.environ.get("VARDYPARTY_ALLOW_EMPTY_APPSETTINGS"):
    missing = [k for k in ("Domain", "ClientId", "Audience") if not str(auth0.get(k) or "").strip()]
    if missing:
        print(f"ERROR: Auth0 fields still empty after merge: {', '.join(missing)}", file=sys.stderr)
        sys.exit(1)
    if not str(api.get("HeadlessBaseUrl") or "").strip():
        print("ERROR: Api.HeadlessBaseUrl still empty after merge", file=sys.stderr)
        sys.exit(1)

text = json.dumps(data, indent=2) + "\n"
path.write_text(text, encoding="utf-8")
print(f"[appsettings] Wrote {path}")
PY
