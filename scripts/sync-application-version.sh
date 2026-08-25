#!/usr/bin/env bash
# Allocate the next ApplicationVersion from origin/main, GitHub -bN releases,
# and (in Actions) other open PRs. Optionally write Version.props (--write).
#
# GitHub.com cannot run a git hook on main. Bump on the feature branch instead
# so the number lands in the already-reviewed PR.
set -euo pipefail

WRITE=0
PROPS="${PROPS:-Version.props}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --write) WRITE=1 ;;
    --props) PROPS="$2"; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

read_version_text() {
  sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' | head -n 1
}

max_num() {
  local best=0 n
  for n in "$@"; do
    [[ -z "${n}" ]] && continue
    if [[ "${n}" =~ ^[0-9]+$ ]] && [[ "${n}" -gt "${best}" ]]; then
      best="${n}"
    fi
  done
  echo "${best}"
}

git fetch --quiet origin main 2>/dev/null || true
MAIN=0
if git cat-file -e origin/main:Version.props 2>/dev/null; then
  MAIN=$(git show origin/main:Version.props | sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' | head -n 1)
fi
MAIN="${MAIN:-0}"

RELEASED=0
OPEN_MAX=0
if command -v gh >/dev/null 2>&1; then
  RELEASED=$(gh release list --limit 50 --json tagName --jq '
    [
      .[].tagName
      | capture("-b(?<n>[0-9]+)$")
      | .n
      | tonumber
    ] | max // 0
  ' 2>/dev/null || echo 0)

  current_branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || true)
  if [[ -n "${GITHUB_REPOSITORY:-}" ]]; then
    while IFS= read -r branch; do
      [[ -z "${branch}" ]] && continue
      [[ "${branch}" == "${current_branch}" ]] && continue
      [[ "${branch}" == "${GITHUB_HEAD_REF:-}" ]] && continue
      body=$(gh api "repos/${GITHUB_REPOSITORY}/contents/Version.props" -f ref="${branch}" --jq '.content' 2>/dev/null || true)
      [[ -z "${body}" ]] && continue
      ver=$(printf '%s' "${body}" | tr -d '\n' | base64 -d 2>/dev/null \
        | sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' | head -n 1 || true)
      OPEN_MAX=$(max_num "${OPEN_MAX}" "${ver}")
    done < <(gh pr list --base main --state open --json headRefName --jq '.[].headRefName' 2>/dev/null || true)
  fi
fi

LOCAL=0
if [[ -f "${PROPS}" ]]; then
  LOCAL=$(read_version_text < "${PROPS}")
fi
LOCAL="${LOCAL:-0}"
RELEASED="${RELEASED:-0}"

FLOOR=$(max_num "${MAIN}" "${RELEASED}" "${OPEN_MAX}")
if [[ "${LOCAL}" -gt "${FLOOR}" ]]; then
  NEXT="${LOCAL}"
else
  NEXT=$((FLOOR + 1))
fi

if [[ "${WRITE}" -eq 1 ]]; then
  if [[ "${LOCAL}" -eq "${NEXT}" ]]; then
    echo "${NEXT}"
    exit 0
  fi
  if [[ ! -f "${PROPS}" ]]; then
    echo "Missing ${PROPS}" >&2
    exit 1
  fi
  sed -i.bak "s/<ApplicationVersion>[0-9]*<\/ApplicationVersion>/<ApplicationVersion>${NEXT}<\/ApplicationVersion>/" "${PROPS}"
  rm -f "${PROPS}.bak"
fi

echo "${NEXT}"
