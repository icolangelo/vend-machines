#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
git_command="git"
git_repo_root="$repo_root"
if [[ -f "$repo_root/.git" ]] && command -v wslpath >/dev/null 2>&1 && ! command -v cygpath >/dev/null 2>&1; then
  git_pointer="$(sed -n 's/^gitdir: //p' "$repo_root/.git")"
  if [[ "$git_pointer" =~ ^[A-Za-z]:[/\\] ]]; then
    if ! command -v git.exe >/dev/null 2>&1; then
      echo "O worktree usa metadados Git do Windows, mas git.exe não está disponível para o scanner." >&2
      exit 2
    fi
    git_command="$(command -v git.exe)"
    git_repo_root="$(wslpath -w "$repo_root")"
  fi
fi

git_dir="$("$git_command" -C "$git_repo_root" rev-parse --path-format=absolute --git-dir)"
git_common_dir="$("$git_command" -C "$git_repo_root" rev-parse --path-format=absolute --git-common-dir)"
worktree_git_args=()
if [[ "$git_dir" != "$git_common_dir" ]]; then
  if [[ "$git_dir" != "$git_common_dir/"* ]]; then
    echo "Não foi possível mapear o diretório Git do worktree para o container do scanner." >&2
    exit 2
  fi

  git_dir_relative="${git_dir#"$git_common_dir"/}"
  git_common_mount="$git_common_dir"
  if command -v cygpath >/dev/null 2>&1; then
    git_common_mount="$(cygpath -m "$git_common_dir")"
  elif command -v wslpath >/dev/null 2>&1; then
    git_common_mount="$(wslpath -u "$git_common_dir")"
  fi
  worktree_git_args=(
    --volume "$git_common_mount:/gitcommon:ro"
    --env "GIT_DIR=/gitcommon/$git_dir_relative"
    --env GIT_WORK_TREE=/path
  )
fi
event_name="${GITHUB_EVENT_NAME:-push}"
head_sha="${SECRET_SCAN_HEAD_SHA:-${GITHUB_SHA:-HEAD}}"
base_sha="${SECRET_SCAN_BASE_SHA:-}"
before_sha="${SECRET_SCAN_BEFORE_SHA:-}"
default_branch="${SECRET_SCAN_DEFAULT_BRANCH:-}"

log_range="$(python3 "$repo_root/tools/Deployment/Determine-SecretScanRange.py" \
  --repo-root "$git_repo_root" \
  --git-command "$git_command" \
  --event "$event_name" \
  --head "$head_sha" \
  --base "$base_sha" \
  --before "$before_sha" \
  --default-branch "$default_branch")"

docker run --rm \
  --volume "$repo_root:/path" \
  "${worktree_git_args[@]}" \
  --env GIT_CONFIG_COUNT=1 \
  --env GIT_CONFIG_KEY_0=safe.directory \
  --env GIT_CONFIG_VALUE_0=/path \
  zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f \
  detect \
  --source=/path \
  --log-opts="$log_range" \
  --redact \
  --no-banner
