#!/usr/bin/env bash
set -euo pipefail

workflow=".github/workflows/deploy.yml"

if [[ ! -f "$workflow" ]]; then
  echo "Workflow not found: $workflow" >&2
  exit 1
fi

grep -Eq 'type=sha,format=long' "$workflow" || {
  echo "Workflow must publish images tagged with the full commit SHA." >&2
  exit 1
}

if grep -Eiq '(^|[^[:alnum:]_-])latest([^[:alnum:]_-]|$)|coolify|COOLIFY_|webhook|curl[[:space:]]|migrations?[[:space:]]+apply|database[[:space:]]+update' "$workflow"; then
  echo "Workflow contains a mutable tag, deployment trigger, credential reference, or automatic migration command." >&2
  exit 1
fi

grep -Eq 'dotnet test .*VendingMachines\.Api\.Tests\.csproj' "$workflow" || {
  echo "Workflow must run backend tests before publishing an image." >&2
  exit 1
}

grep -Eq 'push:[[:space:]]+true' "$workflow" || {
  echo "Workflow must publish the immutable image candidate to GHCR." >&2
  exit 1
}

grep -Eq '^  pull_request:' "$workflow" || {
  echo "Workflow must validate pull requests before they can remove the automatic deploy." >&2
  exit 1
}

validate_job="$(sed -n '/^  validate:/,/^  build-and-push:/p' "$workflow")"
publish_job="$(sed -n '/^  build-and-push:/,$p' "$workflow")"
for expected in 'push: false' 'Run backend tests'; do
  if ! grep -Fq -- "$expected" <<< "$validate_job"; then
    echo "The validation job is missing: $expected" >&2
    exit 1
  fi
done

for expected in "if: github.event_name != 'pull_request'" 'needs: validate' 'push: true'; do
  if ! grep -Fq -- "$expected" <<< "$publish_job"; then
    echo "The publish job is missing: $expected" >&2
    exit 1
  fi
done

if grep -Eq 'push:[[:space:]]+true|packages:[[:space:]]+write' <<< "$validate_job"; then
  echo "The pull request validation job must not publish images or write to GHCR." >&2
  exit 1
fi

echo "Release workflow safety gates passed."
