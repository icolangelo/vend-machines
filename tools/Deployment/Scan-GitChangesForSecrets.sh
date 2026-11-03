#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
event_name="${GITHUB_EVENT_NAME:-push}"
head_sha="${SECRET_SCAN_HEAD_SHA:-${GITHUB_SHA:-HEAD}}"
base_sha="${SECRET_SCAN_BASE_SHA:-}"
before_sha="${SECRET_SCAN_BEFORE_SHA:-}"
default_branch="${SECRET_SCAN_DEFAULT_BRANCH:-}"

log_range="$(python3 "$repo_root/tools/Deployment/Determine-SecretScanRange.py" \
  --repo-root "$repo_root" \
  --event "$event_name" \
  --head "$head_sha" \
  --base "$base_sha" \
  --before "$before_sha" \
  --default-branch "$default_branch")"

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
