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

echo "Release workflow safety gates passed."
