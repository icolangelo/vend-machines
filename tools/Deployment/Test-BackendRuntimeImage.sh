#!/usr/bin/env bash
set -euo pipefail

image="${1:-vend-machines-backend:verify}"
docker image inspect "$image" >/dev/null

environment_configs="$(docker run --rm --network none --entrypoint find "$image" /app -type f -name 'appsettings.*.json' -print)"
if [[ -n "$environment_configs" ]]; then
  echo "::error::A imagem backend contém configuração específica de ambiente: $environment_configs"
  exit 1
fi

local_databases="$(docker run --rm --network none --entrypoint sh "$image" -c "find /app -type f \\( -name '*.db' -o -name '*.db-*' -o -name '*.sqlite' -o -name '*.sqlite3' \\) -print")"
if [[ -n "$local_databases" ]]; then
  echo "::error::A imagem backend contém banco de dados local: $local_databases"
  exit 1
fi

output_file="$(mktemp)"
cleanup() {
  rm -f "$output_file"
}
trap cleanup EXIT

exit_code=0
if timeout 20s docker run --rm --network none \
  --env ASPNETCORE_ENVIRONMENT=Production \
  "$image" >"$output_file" 2>&1; then
  echo "::error::A API iniciou em Production sem a chave de criptografia obrigatória."
  cat "$output_file"
  exit 1
else
  exit_code="$?"
fi

if [[ "$exit_code" -eq 124 ]] || ! grep -Fq 'A configuração Encryption:Key é obrigatória fora do ambiente de desenvolvimento.' "$output_file"; then
  cat "$output_file"
  echo "::error::A API não rejeitou a chave de criptografia ausente antes de iniciar."
  exit 1
fi

echo "Imagem sem arquivos de configuração por ambiente ou banco local; Production rejeita chave de criptografia ausente sem rede (exit $exit_code)."

assert_startup_rejects() {
  local scenario="$1"
  local expected_message="$2"
  shift 2
  local exit_code=0

  if timeout 20s docker run --rm --network none \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env 'Encryption__Key=UniqueRuntimeValidationEncryptionKey2026Current' \
    --env 'Encryption__LegacyKey=UniqueRuntimeValidationEncryptionKey2026Legacy' \
    --env Encryption__WriteLegacyFormat=false \
    "$@" "$image" >"$output_file" 2>&1; then
    echo "::error::A API iniciou em Production com configuração insegura: $scenario."
    cat "$output_file"
    exit 1
  else
    exit_code="$?"
  fi

  if [[ "$exit_code" -eq 124 ]] || ! grep -Fq "$expected_message" "$output_file"; then
    cat "$output_file"
    echo "::error::A API não rejeitou a configuração esperada: $scenario."
    exit 1
  fi

  echo "Production rejeitou a configuração insegura: $scenario (exit $exit_code)."
}

assert_startup_rejects \
  "SQLite" \
  'A produção exige uma conexão PostgreSQL válida' \
  --env 'ConnectionStrings__DefaultConnection=Data Source=/tmp/production-must-not-use-sqlite.db'
assert_startup_rejects \
  "PostgreSQL local" \
  'A produção exige uma conexão PostgreSQL válida' \
  --env 'ConnectionStrings__DefaultConnection=Host=127.0.0.1;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
assert_startup_rejects \
  "PostgreSQL placeholder" \
  'A produção exige uma conexão PostgreSQL válida' \
  --env 'ConnectionStrings__DefaultConnection=Host=YOUR_NEON_HOST;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
assert_startup_rejects \
  "PostgreSQL sem TLS explícito" \
  'SSL Mode=Require, VerifyCA ou VerifyFull' \
  --env 'ConnectionStrings__DefaultConnection=Host=db.production.internal;Database=machines;Username=service;Password=synthetic'
assert_startup_rejects \
  "PostgreSQL válido sem chave JWT" \
  'A configuração Jwt:Key é obrigatória fora do ambiente de desenvolvimento.' \
  --env 'ConnectionStrings__DefaultConnection=Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
assert_startup_rejects \
  "PostgreSQL válido sem segredo Mercado Pago" \
  'A configuração MercadoPago:ClientSecret é obrigatória fora do ambiente de desenvolvimento.' \
  --env 'ConnectionStrings__DefaultConnection=Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode=Require' \
  --env 'Jwt__Key=UniqueRuntimeValidationJwtKey2026Synthetic'
