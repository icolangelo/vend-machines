#!/usr/bin/env bash
set -euo pipefail

health_url="${PRODUCTION_API_HEALTH_URL:-https://api.vendmachine.com.br/health/ready}"
response_file="$(mktemp)"
curl_error_file="$(mktemp)"
trap 'rm -f "$response_file" "$curl_error_file"' EXIT

if ! http_status="$(curl --silent --show-error --retry 4 --retry-delay 2 --max-time 20 \
  --output "$response_file" --write-out '%{http_code}' "$health_url" 2>"$curl_error_file")"; then
  cat "$curl_error_file" >&2
  echo "::error::Não foi possível consultar a readiness da API de produção." >&2
  exit 1
fi

if [[ "$http_status" == "403" ]]; then
  fallback_revision="${PRODUCTION_BACKEND_REF:-}"
  if [[ "$fallback_revision" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::A API de produção bloqueou a Action com HTTP 403; usando PRODUCTION_BACKEND_REF temporariamente como fallback." >&2
    printf '%s\n' "${fallback_revision,,}"
    exit 0
  fi
  cat "$curl_error_file" >&2
  echo "::error::A API de produção respondeu HTTP 403 e não há PRODUCTION_BACKEND_REF válido para fallback." >&2
  exit 1
fi

if [[ "$http_status" != "200" ]]; then
  echo "::error::A API de produção respondeu HTTP $http_status em /health/ready." >&2
  exit 1
fi

health_json="$(cat "$response_file")"

status="$(jq -er '.status | select(type == "string" and . == "Ready")' <<< "$health_json")"
revision="$(jq -er '.sourceCommit | select(type == "string")' <<< "$health_json")"

if [[ ! "$revision" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::A API de produção não anunciou um sourceCommit SHA completo válido." >&2
  exit 1
fi

printf '%s\n' "${revision,,}"
