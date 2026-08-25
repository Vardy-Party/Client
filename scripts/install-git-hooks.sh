#!/usr/bin/env bash
set -euo pipefail
root=$(git rev-parse --show-toplevel)
git config core.hooksPath "${root}/.githooks"
chmod +x "${root}/.githooks/pre-commit" "${root}/scripts/sync-application-version.sh"
echo "core.hooksPath=${root}/.githooks"
