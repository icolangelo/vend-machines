#!/usr/bin/env bash
set -euo pipefail

health_url="${PRODUCTION_API_HEALTH_URL:-https://api.vendmachine.com.br/health/ready}"
health_json="$(curl --fail --silent --show-error --retry 4 --retry-all-errors --retry-delay 2 --max-time 20 "$health_url")"

status="$(jq -er '.status | select(type == "string" and . == "Ready")' <<< "$health_json")"
revision="$(jq -er '.sourceCommit | select(type == "string")' <<< "$health_json")"

if [[ ! "$revision" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::A API de produção não anunciou um sourceCommit SHA completo válido." >&2
  exit 1
fi

printf '%s\n' "${revision,,}"
