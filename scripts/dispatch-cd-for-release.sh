#!/usr/bin/env bash
# Dispatch CD - Package & Release for a Version.props SHA.
#
# CD checks out package_sha and builds every platform from that source, so it
# needs no CI artifacts. This script used to pass ci_run_id = "latest successful
# product CI on main", which ignored the SHA: squash merges that carry
# "[skip ci]" (from the ApplicationVersion bot commit on the PR) never run push
# CI on main, so the newest such run was weeks old, its artifacts had expired
# (5-day retention) and CD #249 failed with "Artifact not found for name:
# build-windows". Product code is gated by PR CI before merge.
#
# Usage: dispatch-cd-for-release.sh <package_sha> <application_version>
set -euo pipefail

PACKAGE_SHA="${1:?package_sha}"
APPLICATION_VERSION="${2:?application_version}"

gh workflow run "CD - Package & Release" --ref main \
  -f produce_release_assets=true \
  -f application_version="${APPLICATION_VERSION}" \
  -f package_sha="${PACKAGE_SHA}"

echo "::notice::Dispatched CD for ${APPLICATION_VERSION} (sha ${PACKAGE_SHA}, built from source)"
