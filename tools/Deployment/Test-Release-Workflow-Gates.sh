#!/usr/bin/env bash
set -euo pipefail

workflow=".github/workflows/deploy.yml"
secret_scanner="tools/Deployment/Scan-GitChangesForSecrets.sh"

if [[ ! -f "$workflow" ]]; then
  echo "Workflow not found: $workflow" >&2
  exit 1
fi

if [[ ! -f "$secret_scanner" ]]; then
  echo "Changed-commit secret scanner not found: $secret_scanner" >&2
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

grep -Fq 'fetch-depth: 0' "$workflow" || {
  echo "The secret scanner requires the full commit history." >&2
  exit 1
}

grep -Fq 'run: bash tools/Deployment/Scan-GitChangesForSecrets.sh' "$workflow" || {
  echo "Workflow must scan changed commits for secrets." >&2
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

grep -Fq 'git rev-list --count' "$secret_scanner" || {
  echo "The secret scanner must reject an empty commit range." >&2
  exit 1
}

grep -Fq 'commits scanned\.' "$secret_scanner" || {
  echo "The secret scanner must confirm that Gitleaks scanned commits." >&2
  exit 1
}

scan_line="$(grep -nF 'run: bash tools/Deployment/Scan-GitChangesForSecrets.sh' "$workflow" | head -n 1 | cut -d: -f1)"
test_line="$(grep -nF 'run: dotnet test' "$workflow" | head -n 1 | cut -d: -f1)"
publish_dependency_line="$(grep -nF 'needs: validate' "$workflow" | head -n 1 | cut -d: -f1)"
if (( scan_line >= test_line || publish_dependency_line <= test_line )); then
  echo "Secret scanning and backend tests must pass before image publication." >&2
  exit 1
fi

echo "Release workflow safety gates passed."
