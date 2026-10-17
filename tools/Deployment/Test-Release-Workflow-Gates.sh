#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/deploy.yml"
verify_workflow="$repo_root/.github/workflows/verify.yml"
frontend_workflow="$repo_root/.github/workflows/frontend-image.yml"
secret_scan_workflow="$repo_root/.github/workflows/secret-scan.yml"
pre_commit_hook="$repo_root/.githooks/pre-commit"
api_dockerignore="$repo_root/apps/backend/VendingMachines.Api/.dockerignore"
temporary_dir="$(mktemp -d)"
trap 'rm -f "$temporary_dir/build-job" "$temporary_dir/release-job"; rmdir "$temporary_dir"' EXIT

if [[ ! -f "$workflow" || ! -f "$verify_workflow" || ! -f "$frontend_workflow" || ! -f "$secret_scan_workflow" || ! -f "$pre_commit_hook" || ! -f "$api_dockerignore" ]]; then
  echo "Workflows de verificação, deploy e publicação frontend não encontrados." >&2
  exit 1
fi

secret_scan="$repo_root/tools/Deployment/Scan-GitChangesForSecrets.sh"
rolling_update_validator="$repo_root/tools/Deployment/Validate-Coolify-RollingUpdate.py"
github_environment_validator="$repo_root/tools/Deployment/validate_github_production_environment.py"
github_environment_test="$repo_root/tools/Deployment/test_github_production_environment.py"
if [[ ! -f "$secret_scan" ]]; then
  echo "Scanner incremental de segredos não encontrado." >&2
  exit 1
fi
if [[ ! -f "$github_environment_validator" ]]; then
  echo "Validador de proteção do ambiente GitHub production não encontrado." >&2
  exit 1
fi
if [[ ! -f "$github_environment_test" ]]; then
  echo "Testes do validador de proteção do ambiente GitHub production não encontrados." >&2
  exit 1
fi
if [[ ! -f "$rolling_update_validator" ]]; then
  echo "Validador de elegibilidade para rolling update do Coolify não encontrado." >&2
  exit 1
fi

sed -n '/^  build-and-push:/,/^  deploy-production:/p' "$workflow" > "$temporary_dir/build-job"
sed -n '/^  deploy-production:/,$p' "$workflow" > "$temporary_dir/release-job"

require_contains() {
  local file="$1"
  local expected="$2"
  if ! grep -Fq -- "$expected" "$file"; then
    echo "Gate ausente no workflow: $expected" >&2
    exit 1
  fi
}

require_contains "$pre_commit_hook" 'gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f'
require_contains "$pre_commit_hook" 'protect'
require_contains "$pre_commit_hook" '--staged'
require_contains "$api_dockerignore" '.tmp/'
require_contains "$api_dockerignore" 'bin/'
require_contains "$api_dockerignore" 'obj/'
require_contains "$api_dockerignore" '*.db'
require_contains "$workflow" 'type=raw,value=${{ github.sha }}'
require_contains "$workflow" 'VENDMACHINES_DESIGNTIME_POSTGRES:'
require_contains "$workflow" 'image: postgres:17'
require_contains "$workflow" 'VENDMACHINES_REQUIRE_TEST_POSTGRES: "true"'
require_contains "$verify_workflow" 'VENDMACHINES_REQUIRE_TEST_POSTGRES: "true"'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeviceMessageInboxTests.cs" 'VENDMACHINES_REQUIRE_TEST_POSTGRES'
require_contains "$verify_workflow" 'Verify Production startup rejects unsafe database settings'
require_contains "$verify_workflow" 'Data Source=/tmp/production-must-not-use-sqlite.db'
require_contains "$verify_workflow" 'Host=127.0.0.1;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
require_contains "$verify_workflow" 'Host=YOUR_NEON_HOST;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
require_contains "$verify_workflow" 'Host=db.production.internal;Database=machines;Username=service;Password=synthetic'
require_contains "$verify_workflow" 'PostgreSQL válido sem chave JWT'
require_contains "$verify_workflow" 'PostgreSQL válido sem segredo Mercado Pago'
require_contains "$verify_workflow" 'A produção exige uma conexão PostgreSQL válida'
require_contains "$verify_workflow" 'A configuração Jwt:Key é obrigatória fora do ambiente de desenvolvimento.'
require_contains "$verify_workflow" 'A configuração MercadoPago:ClientSecret é obrigatória fora do ambiente de desenvolvimento.'
require_contains "$verify_workflow" 'SSL Mode=Require, VerifyCA ou VerifyFull'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'ProductionDatabaseConfiguration.Validate'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Data/ProductionDatabaseConfiguration.cs" 'SslMode.VerifyFull'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/ProductionDatabaseConfigurationTests.cs" 'AcceptsExplicitPostgresTlsModes'
require_contains "$workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$verify_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$frontend_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$secret_scan_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$secret_scan_workflow" '  push:'
require_contains "$secret_scan_workflow" '  pull_request:'
require_contains "$secret_scan_workflow" '  workflow_dispatch:'
require_contains "$secret_scan_workflow" 'permissions:'
require_contains "$secret_scan_workflow" '  contents: read'
require_contains "$workflow" 'fetch-depth: 0'
require_contains "$verify_workflow" 'fetch-depth: 0'
require_contains "$frontend_workflow" 'fetch-depth: 0'
require_contains "$secret_scan_workflow" 'fetch-depth: 0'
require_contains "$secret_scan" 'zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f'
deploy_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
deploy_publish_line="$(grep -nF 'name: Build and Push Docker Image' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
verify_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$verify_workflow" | head -n 1 | cut -d: -f1)"
verify_test_line="$(grep -nF 'name: Run backend tests' "$verify_workflow" | head -n 1 | cut -d: -f1)"
frontend_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
frontend_publish_line="$(grep -nF 'name: Publish tested candidate image' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
if (( deploy_scan_line >= deploy_publish_line || verify_scan_line >= verify_test_line || frontend_scan_line >= frontend_publish_line )); then
  echo "O scan de segredos deve passar antes dos testes e da publicação de imagens." >&2
  exit 1
fi
require_contains "$temporary_dir/build-job" 'migrations has-pending-model-changes'
require_contains "$temporary_dir/build-job" 'before_session_index=20260715023221_AddTelemetrySessionManagement'
require_contains "$temporary_dir/build-job" 'session_index=20260730120000_AddMdbStatusAndAutomaticSession'
require_contains "$temporary_dir/build-job" 'before_payment_index=20260926173525_AddWebhookInbox'
require_contains "$temporary_dir/build-job" 'payment_index=20260926174359_AddPaymentReconciliationTracking'
require_contains "$temporary_dir/build-job" 'migrations script 0 "$before_session_index" --idempotent'
require_contains "$temporary_dir/build-job" 'migrations script "$before_session_index" "$session_index"'
require_contains "$temporary_dir/build-job" 'migrations script "$session_index" "$before_payment_index" --idempotent'
require_contains "$temporary_dir/build-job" 'migrations script "$before_payment_index" "$payment_index"'
require_contains "$temporary_dir/build-job" 'migrations script "$payment_index" --idempotent'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-before-session-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migration-session-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-between-online-indexes.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migration-payment-reconciliation-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-after-online-indexes.sql'
require_contains "$temporary_dir/build-job" 'path: artifacts/postgresql-migration*.sql'
require_contains "$temporary_dir/build-job" 'name: Verify online migration segments are outside EF idempotency wrappers'
segment_validation_line="$(grep -nF 'name: Verify online migration segments are outside EF idempotency wrappers' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
migration_upload_line="$(grep -nF 'name: Upload migration script for pre-deploy review' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
if (( segment_validation_line >= migration_upload_line )); then
  echo "Os segmentos concorrentes precisam ser validados antes de publicar o artefato SQL." >&2
  exit 1
fi
require_contains "$temporary_dir/build-job" 'dotnet list "$project" package --vulnerable --include-transitive'
require_contains "$verify_workflow" 'dotnet list "$project" package --vulnerable --include-transitive'
require_contains "$temporary_dir/build-job" 'tools/VendingMachines.Simulator/VendingMachines.Simulator.csproj'
require_contains "$temporary_dir/build-job" 'tools/VendingMachines.EncryptionRotation/VendingMachines.EncryptionRotation.csproj'
require_contains "$verify_workflow" 'tools/VendingMachines.Simulator/VendingMachines.Simulator.csproj'
require_contains "$verify_workflow" 'tools/VendingMachines.EncryptionRotation/VendingMachines.EncryptionRotation.csproj'
if grep -Eq 'type=[^[:space:]]*value=latest' "$workflow"; then
  echo "A build automática não pode publicar a tag latest." >&2
  exit 1
fi

for gate in \
  "github.event_name == 'workflow_dispatch'" \
  "inputs.confirm_production_deploy == 'yes'" \
  "inputs.database_migrations_applied == 'yes'" \
  "inputs.backup_verified == 'yes'" \
  "inputs.production_secrets_rotated == 'yes'" \
  "inputs.telemetry_reconnect_tested == 'yes'" \
  "inputs.active_vend_sessions_drained == 'yes'" \
  "inputs.machine_connections_preserved == 'yes'" \
  "github.ref == 'refs/heads/main'"; do
  require_contains "$temporary_dir/release-job" "$gate"
done

if grep -Fq 'Coolify-Deployment.sh deploy' "$temporary_dir/build-job"; then
  echo "O job de build não pode acionar Coolify." >&2
  exit 1
fi
if grep -Eiq 'coolify|COOLIFY_BACKEND_DEPLOY_WEBHOOK|COOLIFY_API_TOKEN|Trigger Coolify Deploy' "$temporary_dir/build-job"; then
  echo "O job de build não pode conter integração ou credenciais do Coolify." >&2
  exit 1
fi
if grep -Eiq 'dotnet(-ef)?[[:space:]]+.*database[[:space:]]+update|migrations[[:space:]]+apply' "$workflow"; then
  echo "O workflow de release deve publicar o script de migrations para revisão, sem aplicá-las automaticamente." >&2
  exit 1
fi
require_contains "$temporary_dir/release-job" 'Coolify-Deployment.sh deploy'
require_contains "$temporary_dir/release-job" 'Coolify-Deployment.sh preflight'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'A API atual não confirmou /health/ready = Ready'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'PRODUCTION_API_DEPLOY_READY_URL'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" '/health/deploy-ready'
require_contains "$repo_root/.github/workflows/deploy.yml" 'PRODUCTION_API_DEPLOY_READY_URL: https://api.vendmachine.com.br/health/deploy-ready'
require_contains "$repo_root/.github/workflows/deploy.yml" 'ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT:'
require_contains "$repo_root/.github/workflows/deploy.yml" 'bootstrap_deploy_readiness_endpoint:'
require_contains "$repo_root/.github/workflows/deploy.yml" "inputs.bootstrap_deploy_readiness_endpoint == 'yes'"
require_contains "$repo_root/.github/workflows/deploy.yml" 'production_secrets_rotated:'
secrets_rotation_input="$(sed -n '/^      production_secrets_rotated:/,/^      telemetry_reconnect_tested:/p' "$workflow")"
if ! printf '%s\n' "$secrets_rotation_input" | grep -Fq 'default: "no"' ||
   ! printf '%s\n' "$secrets_rotation_input" | grep -Fq 'required: true' ||
   ! printf '%s\n' "$secrets_rotation_input" | grep -Fq 'type: choice'; then
  echo "A confirmação de rotação de segredos deve ser obrigatória e começar em no." >&2
  exit 1
fi
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'app.MapGet("/health/deploy-ready"'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'telemetry_commands_in_flight'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentReadinessServiceTests.cs" 'DeploymentPreflightBlocksOpenSalesAndUnresolvedMachineCommands'
if (( $(grep -cF 'validate_production_deploy_readiness' "$repo_root/tools/Deployment/Coolify-Deployment.sh") < 3 )); then
  echo "O gate de atividade operacional precisa existir e ser consultado antes da promoção e antes do webhook." >&2
  exit 1
fi
last_deploy_preflight_line="$(grep -nF 'validate_production_deploy_readiness "$response_dir/deploy-ready.json"' "$repo_root/tools/Deployment/Coolify-Deployment.sh" | tail -n 1 | cut -d: -f1)"
trigger_webhook_line="$(grep -nF 'trigger_response="$response_dir/trigger.json"' "$repo_root/tools/Deployment/Coolify-Deployment.sh" | head -n 1 | cut -d: -f1)"
if [[ -z "$last_deploy_preflight_line" || -z "$trigger_webhook_line" ]] || (( last_deploy_preflight_line >= trigger_webhook_line )); then
  echo "A atividade de vendas/comandos precisa ser consultada antes do webhook do Coolify." >&2
  exit 1
fi
preflight_line="$(grep -nF 'Coolify-Deployment.sh preflight' "$temporary_dir/release-job" | head -n 1 | cut -d: -f1)"
promote_line="$(grep -nF 'name: Promote immutable image to production tag' "$temporary_dir/release-job" | head -n 1 | cut -d: -f1)"
if (( preflight_line >= promote_line )); then
  echo "A readiness atual da API precisa ser confirmada antes de promover latest." >&2
  exit 1
fi
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'Validate-Coolify-RollingUpdate.py'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'validate_coolify_application "$response_dir/application.json"'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'Rolling update started'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'New container is healthy'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'Rolling update completed'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'ShutdownTimeout = TimeSpan.FromSeconds(45)'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'resource_uuid == sys.argv[2]'
require_contains "$rolling_update_validator" 'ports_mappings'
require_contains "$rolling_update_validator" 'is_auto_deploy_enabled'
require_contains "$rolling_update_validator" 'docker_registry_image_name'
require_contains "$rolling_update_validator" 'docker_registry_image_tag'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" '"${REGISTRY,,}/${IMAGE_NAME,,}" "latest"'
require_contains "$rolling_update_validator" 'expected_host not in configured_hosts'
require_contains "$rolling_update_validator" 'health_check_path'
require_contains "$rolling_update_validator" 'stop_grace_period'
require_contains "$repo_root/tools/Deployment/Test-Coolify-Deployment.sh" 'rolling-auto-deploy-missing'
require_contains "$temporary_dir/release-job" 'uses: actions/checkout@v4'
require_contains "$temporary_dir/release-job" 'environment: production'
require_contains "$temporary_dir/release-job" 'Verify GitHub production environment protection'
require_contains "$temporary_dir/release-job" 'validate_github_production_environment.py'
require_contains "$temporary_dir/release-job" 'actions: read'
require_contains "$temporary_dir/release-job" 'GITHUB_ADMIN_READ_TOKEN: ${{ secrets.GITHUB_ADMIN_READ_TOKEN }}'
require_contains "$github_environment_validator" 'Configure GITHUB_ADMIN_READ_TOKEN no ambiente production'
if grep -Eq '^[[:space:]]+administration:' "$temporary_dir/release-job"; then
  echo "O workflow não pode declarar um escopo GITHUB_TOKEN inexistente para Administration." >&2
  exit 1
fi
require_contains "$github_environment_validator" '/branches/main/protection'
require_contains "$github_environment_validator" 'Verify application / Backend tests'
require_contains "$github_environment_validator" 'Verify application / Frontend types, lint, tests and build'
require_contains "$github_environment_validator" 'Scan commit changes for secrets / Gitleaks changed commits'
environment_check_line="$(grep -nF 'validate_github_production_environment.py' "$temporary_dir/release-job" | head -n 1 | cut -d: -f1)"
promote_line_for_environment="$(grep -nF 'name: Promote immutable image to production tag' "$temporary_dir/release-job" | head -n 1 | cut -d: -f1)"
if (( environment_check_line >= promote_line_for_environment )); then
  echo "A proteção do ambiente production precisa ser validada antes de promover latest." >&2
  exit 1
fi
require_contains "$temporary_dir/release-job" "github.ref == 'refs/heads/main'"
if grep -Fq "github.ref == 'refs/heads/master'" "$temporary_dir/release-job"; then
  echo "A publicação production deve aceitar somente a branch padrão main." >&2
  exit 1
fi
python3 "$github_environment_test"
require_contains "$workflow" 'machine_connections_preserved:'
require_contains "$workflow" 'active_vend_sessions_drained:'
session_gate_description="$(sed -n '/^      active_vend_sessions_drained:/,/^      machine_connections_preserved:/p' "$workflow")"
if ! grep -Fq 'default: "no"' <<<"$session_gate_description" ||
   ! grep -Fq 'zero sessões físicas de venda abertas' <<<"$session_gate_description"; then
  echo "A release precisa começar bloqueada até confirmar ausência de sessões de venda abertas." >&2
  exit 1
fi
require_contains "$repo_root/docs/deploy-seguro.md" 'docs/deploy-preflight.sql'
require_contains "$repo_root/docs/deploy-seguro.md" 'active_vend_sessions = 0'
require_contains "$repo_root/docs/deploy-preflight.sql" 'BEGIN TRANSACTION READ ONLY;'
require_contains "$repo_root/docs/deploy-preflight.sql" "SET LOCAL statement_timeout = '5s';"
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentPreflightSqlTests.cs" 'ReadOnlyPreflightCountsOpenPhysicalSessionsWithoutReturningIdentifiers'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/VendingMachines.Api.Tests.csproj" 'deploy-preflight.sql'
require_contains "$temporary_dir/release-job" 'inputs.continuity_test_evidence_url != '\''none'\'''
require_contains "$workflow" 'Validate telemetry continuity evidence link'
gate_description="$(sed -n '/^      machine_connections_preserved:/,/^      continuity_test_evidence_url:/p' "$workflow")"
if ! grep -Fq 'default: "no"' <<<"$gate_description" ||
   ! grep -Fq 'Reconexão posterior, sozinha, não atende.' <<<"$gate_description"; then
  echo "O gate de sockets deve começar bloqueado e distinguir continuidade de reconexão posterior." >&2
  exit 1
fi
evidence_description="$(awk '/^      continuity_test_evidence_url:/{in_section=1; next} in_section && /^      [a-z_]+:/{exit} in_section{print}' "$workflow")"
if ! grep -Fq 'default: "none"' <<<"$evidence_description"; then
  echo "O link de evidência de continuidade deve começar vazio/bloqueado." >&2
  exit 1
fi
python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/example/repository/actions/runs/123" >/dev/null
if python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "http://github.com/example/repository/actions/runs/123" >/dev/null 2>&1; then
  echo "O validador aceitou evidência de continuidade em HTTP sem TLS." >&2
  exit 1
fi
if python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://user:password@example.com/report" >/dev/null 2>&1; then
  echo "O validador aceitou credenciais embutidas no link de evidência." >&2
  exit 1
fi
if python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://[invalid/report" >/dev/null 2>&1; then
  echo "O validador aceitou um link malformado como evidência." >&2
  exit 1
fi
require_contains "$verify_workflow" 'Test-Release-Workflow-Gates.sh'
require_contains "$verify_workflow" 'Test-Coolify-Deployment.sh'

require_contains "$frontend_workflow" 'type=sha,format=long'
require_contains "$frontend_workflow" 'Build immutable candidate image'
require_contains "$frontend_workflow" 'push: false'
require_contains "$frontend_workflow" 'load: true'
require_contains "$frontend_workflow" 'Publish tested candidate image'
require_contains "$frontend_workflow" 'tags: ${{ steps.meta.outputs.tags }}'
require_contains "$frontend_workflow" 'docker push ${{ steps.meta.outputs.tags }}'
require_contains "$frontend_workflow" 'docker run -d --name frontend-image-smoke --publish 127.0.0.1:8088:8080 ${{ steps.meta.outputs.tags }}'
for gate in \
  'npm audit --audit-level=moderate' \
  'npx tsc --noEmit -p tsconfig.app.json' \
  'npm run lint' \
  'npm test' \
  'npx playwright install --with-deps chromium' \
  'npm run test:e2e' \
  'npm run test:e2e:production'; do
  require_contains "$frontend_workflow" "$gate"
  gate_line="$(grep -nF "$gate" "$frontend_workflow" | head -n 1 | cut -d: -f1)"
  publish_line="$(grep -nF 'Publish tested candidate image' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
  if (( gate_line >= publish_line )); then
    echo "O gate '$gate' deve passar antes da publicação da imagem frontend." >&2
    exit 1
  fi
done
build_line="$(grep -nF 'Build immutable candidate image' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
smoke_line="$(grep -nF 'Test production image routes, headers and CSP' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
if (( build_line >= smoke_line || smoke_line >= publish_line )); then
  echo "O mesmo artefato frontend deve ser construído, testado e só então publicado." >&2
  exit 1
fi
if grep -Eq 'value=latest|tags:[[:space:]]*latest|Coolify|push:[[:space:]]*true' "$frontend_workflow"; then
  echo "A publicação automática do frontend não pode mover latest nem acionar Coolify." >&2
  exit 1
fi

echo "Release workflow gates: API exige confirmação completa; frontend publica somente SHA depois de todos os gates e não aciona Coolify."
