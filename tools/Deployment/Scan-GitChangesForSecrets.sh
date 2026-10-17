#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ -f "$repo_root/.git" ]]; then
  git_dir="$(sed -n 's/^gitdir: //p' "$repo_root/.git")"
  if [[ "$git_dir" =~ ^[A-Za-z]:[/\\] ]]; then
    if command -v wslpath >/dev/null 2>&1; then
      git_dir="$(wslpath -u "$git_dir")"
    elif command -v cygpath >/dev/null 2>&1; then
      git_dir="$(cygpath -u "$git_dir")"
    fi
  fi
  if [[ -z "$git_dir" || ! -d "$git_dir" ]]; then
    echo "The worktree Git directory could not be resolved." >&2
    exit 2
  fi
  git_dir="$(cd "$git_dir" && pwd -P)"
  if [[ -f "$git_dir/commondir" ]]; then
    common_dir_relative="$(cat "$git_dir/commondir")"
    git_common_dir="$(cd "$git_dir/$common_dir_relative" && pwd -P)"
  else
    git_common_dir="$git_dir"
  fi
else
  git_dir="$(git -C "$repo_root" rev-parse --absolute-git-dir)"
  git_common_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)"
fi

if [[ "$git_dir" == "$git_common_dir" ]]; then
  container_git_dir="/git"
elif [[ "$git_dir" == "$git_common_dir/"* ]]; then
  container_git_dir="/git/${git_dir#"$git_common_dir"/}"
else
  echo "Git metadata directory must be within the common Git directory." >&2
  exit 2
fi

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
  parent_sha="$(GIT_DIR="$git_dir" GIT_WORK_TREE="$repo_root" git rev-parse "${head_sha}^")"
  log_range="$parent_sha..$head_sha"
fi

commit_count="$(GIT_DIR="$git_dir" GIT_WORK_TREE="$repo_root" git rev-list --count "$log_range")"
if [[ ! "$commit_count" =~ ^[1-9][0-9]*$ ]]; then
  echo "Secret scan range contains no commits: $log_range" >&2
  exit 2
fi

scan_output="$(mktemp)"
trap 'rm -f "$scan_output"' EXIT
if docker run --rm \
  --volume "$repo_root:/path" \
  --volume "$git_common_dir:/git:ro" \
  --env GIT_DIR="$container_git_dir" \
  --env GIT_WORK_TREE=/path \
  --env GIT_CONFIG_COUNT=1 \
  --env GIT_CONFIG_KEY_0=safe.directory \
  --env GIT_CONFIG_VALUE_0=/path \
  zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f \
  detect \
  --source=/path \
  --log-opts="$log_range" \
  --redact \
  --no-banner >"$scan_output" 2>&1; then
  :
else
  scan_status="$?"
  cat "$scan_output"
  exit "$scan_status"
fi

cat "$scan_output"
if ! grep -Eq '[1-9][0-9]* commits scanned\.' "$scan_output"; then
  echo "Gitleaks completed without confirming that it scanned any commits." >&2
  exit 2
fi
