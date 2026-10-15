#!/usr/bin/env bash
# Dispatch CD - Package & Release for a Version.props SHA, using the latest
# successful *product* CI on main (not a ci: bump commit — those skip platform
# builds and have no artifacts).
#
# Usage: dispatch-cd-for-release.sh <package_sha> <application_version>
set -euo pipefail

PACKAGE_SHA="${1:?package_sha}"
APPLICATION_VERSION="${2:?application_version}"

CI_RUN_ID=""
for _ in $(seq 1 20); do
  CI_RUN_ID="$(
    gh run list --workflow "CI - Build & Test" --branch main --status success --limit 30 \
      --json databaseId,displayTitle --jq '
        [.[] | select(.displayTitle | test("^ci: (bump|auto-increment)") | not)]
        | .[0].databaseId // empty
      '
  )"
  if [[ -n "${CI_RUN_ID}" ]]; then
    break
  fi
  echo "Waiting for a successful product CI on main..."
  sleep 30
done

if [[ -z "${CI_RUN_ID}" ]]; then
  echo "::error::No successful product CI on main. CD needs those artifacts. Re-run after main CI completes."
  exit 1
fi

gh workflow run "CD - Package & Release" --ref main \
  -f ci_run_id="${CI_RUN_ID}" \
  -f produce_release_assets=true \
  -f application_version="${APPLICATION_VERSION}" \
  -f package_sha="${PACKAGE_SHA}"

echo "::notice::Dispatched CD for ${APPLICATION_VERSION} (CI ${CI_RUN_ID}, sha ${PACKAGE_SHA})"
