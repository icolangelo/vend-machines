#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
event_name="${GITHUB_EVENT_NAME:-push}"
head_sha="${SECRET_SCAN_HEAD_SHA:-${GITHUB_SHA:-HEAD}}"
base_sha="${SECRET_SCAN_BASE_SHA:-}"
before_sha="${SECRET_SCAN_BEFORE_SHA:-}"

if [[ "$event_name" == "pull_request" ]]; then
  if [[ -z "$base_sha" || -z "$head_sha" ]]; then
    echo "SECRET_SCAN_BASE_SHA e SECRET_SCAN_HEAD_SHA são obrigatórios em pull requests." >&2
    exit 2
  fi
  log_range="$base_sha..$head_sha"
elif [[ -n "$before_sha" && ! "$before_sha" =~ ^0+$ ]]; then
  log_range="$before_sha..$head_sha"
else
  parent_sha="$(git -C "$repo_root" rev-parse "${head_sha}^")"
  log_range="$parent_sha..$head_sha"
fi

docker run --rm \
  --volume "$repo_root:/path" \
  --env GIT_CONFIG_COUNT=1 \
  --env GIT_CONFIG_KEY_0=safe.directory \
  --env GIT_CONFIG_VALUE_0=/path \
  zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f \
  detect \
  --source=/path \
  --log-opts="$log_range" \
  --redact \
  --no-banner
