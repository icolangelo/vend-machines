#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/deploy.yml"
verify_workflow="$repo_root/.github/workflows/verify.yml"
frontend_workflow="$repo_root/.github/workflows/frontend-image.yml"
secret_scan_workflow="$repo_root/.github/workflows/secret-scan.yml"
pre_commit_hook="$repo_root/.githooks/pre-commit"
api_dockerignore="$repo_root/apps/backend/VendingMachines.Api/.dockerignore"
backend_context_test="$repo_root/tools/Deployment/test_backend_docker_context.py"
temporary_dir="$(mktemp -d)"
trap 'rm -f "$temporary_dir/build-job" "$temporary_dir/release-job"; rmdir "$temporary_dir"' EXIT

if [[ ! -f "$workflow" || ! -f "$verify_workflow" || ! -f "$frontend_workflow" || ! -f "$secret_scan_workflow" || ! -f "$pre_commit_hook" || ! -f "$api_dockerignore" || ! -f "$backend_context_test" ]]; then
  echo "Workflows de verificação, deploy e publicação frontend não encontrados." >&2
  exit 1
fi

secret_scan="$repo_root/tools/Deployment/Scan-GitChangesForSecrets.sh"
secret_scan_range="$repo_root/tools/Deployment/Determine-SecretScanRange.py"
secret_scan_range_test="$repo_root/tools/Deployment/test_secret_scan_range.py"
rolling_update_validator="$repo_root/tools/Deployment/Validate-Coolify-RollingUpdate.py"
github_environment_validator="$repo_root/tools/Deployment/validate_github_production_environment.py"
github_environment_test="$repo_root/tools/Deployment/test_github_production_environment.py"
if [[ ! -f "$secret_scan" ]]; then
  echo "Scanner incremental de segredos não encontrado." >&2
  exit 1
fi
if [[ ! -f "$secret_scan_range" || ! -f "$secret_scan_range_test" ]]; then
  echo "Seletor ou testes de intervalo do scanner de segredos não encontrados." >&2
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
require_contains "$api_dockerignore" '**/*.db-*'
require_contains "$api_dockerignore" '**/*.sqlite-*'
require_contains "$workflow" 'python -B tools/Deployment/test_backend_docker_context.py'
require_contains "$verify_workflow" 'python -B tools/Deployment/test_backend_docker_context.py'
require_contains "$verify_workflow" 'node tools/Deployment/CheckFrontendApiContract.mjs'
require_contains "$verify_workflow" 'node --test tools/Deployment/test_frontend_api_contract.mjs'
require_contains "$verify_workflow" 'id: frontend-target'
require_contains "$verify_workflow" 'git diff --quiet "$base_ref" "$GITHUB_SHA" -- apps/frontend'
require_contains "$verify_workflow" "if: steps.frontend-target.outputs.changed == 'true'"
require_contains "$verify_workflow" 'BACKEND_COMPAT_REF: ${{ steps.frontend-target.outputs.base_ref }}'
require_contains "$verify_workflow" '--backend-ref "$backend_ref"'
frontend_checkout="$(sed -n '/^  frontend-checks:/,$p' "$verify_workflow" | sed -n '/^[[:space:]]*- name: Checkout code$/,/^[[:space:]]*- name: Set up Node.js$/p')"
if ! grep -Fq 'fetch-depth: 0' <<< "$frontend_checkout"; then
  echo "O job frontend-checks precisa buscar o histórico-base completo para validar a API de destino." >&2
  exit 1
fi
require_contains "$frontend_workflow" 'node tools/Deployment/CheckFrontendApiContract.mjs'
require_contains "$frontend_workflow" 'node --test tools/Deployment/test_frontend_api_contract.mjs'
require_contains "$frontend_workflow" 'BACKEND_COMPAT_REF: ${{ github.event.before }}'
require_contains "$frontend_workflow" '--backend-ref "$backend_ref"'
require_contains "$frontend_workflow" 'tools/Deployment/CheckFrontendApiContract.mjs'
require_contains "$frontend_workflow" 'tools/Deployment/test_frontend_api_contract.mjs'
if grep -Fq 'tools/Deployment/**' "$frontend_workflow"; then
  echo "A publicação da imagem frontend não deve disparar por alterações backend-only em tools/Deployment." >&2
  exit 1
fi
require_contains "$workflow" 'node tools/Deployment/CheckFrontendApiContract.mjs'
require_contains "$workflow" 'node --test tools/Deployment/test_frontend_api_contract.mjs'
require_contains "$workflow" 'FRONTEND_COMPAT_REF: ${{ github.event.before }}'
require_contains "$workflow" '--frontend-ref "$frontend_ref"'
require_contains "$backend_context_test" 'sensitive_field_detector_is_valid'
require_contains "$backend_context_test" 'find_populated_sensitive_fields'
require_contains "$backend_context_test" 'ls-files'
require_contains "$backend_context_test" '**/*.db-*'
require_contains "$workflow" 'type=raw,value=${{ github.sha }}'
backend_tag_rules="$(sed -n '/^[[:space:]]*- name: Extract metadata (tags, labels)/,/^[[:space:]]*- name: Build candidate backend image without publishing/p' "$workflow" | sed -n '/^[[:space:]]*tags: |$/,/^[[:space:]]*labels: /p' | sed '1d;$d' | sed 's/^[[:space:]]*//')"
if [[ "$backend_tag_rules" != 'type=raw,value=${{ github.sha }}' ]]; then
  echo "O build automático do backend deve publicar somente a tag imutável do SHA completo." >&2
  exit 1
fi
require_contains "$workflow" 'VENDMACHINES_DESIGNTIME_POSTGRES:'
require_contains "$workflow" 'image: postgres:17'
require_contains "$workflow" 'POSTGRES_INITDB_ARGS: "-c max_connections=300"'
require_contains "$verify_workflow" 'POSTGRES_INITDB_ARGS: "-c max_connections=300"'
require_contains "$workflow" 'VENDMACHINES_REQUIRE_TEST_POSTGRES: "true"'
require_contains "$verify_workflow" 'VENDMACHINES_REQUIRE_TEST_POSTGRES: "true"'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeviceMessageInboxTests.cs" 'VENDMACHINES_REQUIRE_TEST_POSTGRES'
runtime_image_test="$repo_root/tools/Deployment/Test-BackendRuntimeImage.sh"
require_contains "$verify_workflow" 'bash tools/Deployment/Test-BackendRuntimeImage.sh vend-machines-backend:verify'
require_contains "$workflow" 'bash tools/Deployment/Test-BackendRuntimeImage.sh "$BACKEND_IMAGE_TAG"'
require_contains "$runtime_image_test" 'Data Source=/tmp/production-must-not-use-sqlite.db'
require_contains "$runtime_image_test" 'Host=127.0.0.1;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
require_contains "$runtime_image_test" 'Host=YOUR_NEON_HOST;Database=machines;Username=service;Password=synthetic;SSL Mode=Require'
require_contains "$runtime_image_test" 'Host=db.production.internal;Database=machines;Username=service;Password=synthetic'
require_contains "$runtime_image_test" 'PostgreSQL válido sem chave JWT'
require_contains "$runtime_image_test" 'PostgreSQL válido sem segredo Mercado Pago'
require_contains "$runtime_image_test" 'A produção exige uma conexão PostgreSQL válida'
require_contains "$runtime_image_test" 'A configuração Jwt:Key é obrigatória fora do ambiente de desenvolvimento.'
require_contains "$runtime_image_test" 'A configuração MercadoPago:ClientSecret é obrigatória fora do ambiente de desenvolvimento.'
require_contains "$runtime_image_test" 'SSL Mode=Require, VerifyCA ou VerifyFull'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Migrations/20261025180955_AddSessionAdmissionGate.cs" 'TR_MachineSessions_RejectWhenDeploymentPaused'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Migrations/20261025180955_AddSessionAdmissionGate.cs" 'FOR SHARE'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Migrations/20261025185728_AddTelemetryCommandResolution.cs" 'CK_TelemetryCommandResolutions_OneCommand'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Migrations/20261025185728_AddTelemetryCommandResolution.cs" 'ValidateTelemetryCommandResolutionTarget'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/PostgresMigrationChainTests.cs" 'telemetry_command_not_unresolved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'active_session_without_routable_machine'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'unresolved_session_closures'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Controllers/TelemetryController.cs" 'LockForSessionStartAsync(cancellationToken)'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'AddTelemetryConnectionOperations'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/TelemetryConnectionServiceCollectionExtensions.cs" 'AddSingleton<ITelemetryCommandSender>'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/TelemetryControllerIsolationTests.cs" 'ManualCommandIsNotSentWhileDeploymentAdmissionIsPaused'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/TelemetryCommandAdmissionTests.cs" 'PauseWaitsForAdmittedManualCommandToFinish'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/PostgresMigrationChainTests.cs" 'A pausa precisa esperar a gravação de abertura já admitida.'
require_contains "$repo_root/tools/Database/Pause-SessionAdmission.sql" 'SET "NewSessionsPaused" = TRUE'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'SET "NewSessionsPaused" = FALSE'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'FOR UPDATE'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'AS active_vend_sessions'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'AS unresolved_session_closures'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'AS unresolved_machine_commands'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'AS invalid_platform_fee_settings'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'ROLLBACK;'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'RAISE EXCEPTION'
require_contains "$repo_root/tools/Database/Pause-SessionAdmission.sql" 'RAISE EXCEPTION'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'Assert-ResumeBlocked'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'MdbStatus'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'CREATE ROLE session_admission_operator LOGIN'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT SELECT ("ClosedAt", "StartedAt", "State") ON "MachineSessions"'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT SELECT ("Id", "OwnerNodeId", "Status", "Operation", "Error") ON "TelemetryCommandDispatches"'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT SELECT ("Id", "Status", "Error") ON "TelemetryCommands"'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'ACK local incerto'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'Resolve-TelemetryCommand.sql'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'List-UnresolvedTelemetryCommands.sql'
require_contains "$repo_root/tools/Database/List-UnresolvedTelemetryCommands.sql" 'BEGIN TRANSACTION READ ONLY;'
require_contains "$repo_root/tools/Database/Resolve-TelemetryCommand.sql" 'telemetry_command_not_unresolved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'TelemetryCommandResolutions'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/TelemetryConnectionManager.cs" 'resolution.TelemetryCommandDispatchId == x.Id'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT SELECT ("TelemetryCommandId", "TelemetryCommandDispatchId") ON "TelemetryCommandResolutions"'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT INSERT ON "TelemetryCommandResolutions"'
require_contains "$repo_root/tools/Database/Test-SessionAdmissionScripts.ps1" 'GRANT SELECT ("ApplicationFeePercent") ON "SystemSettings"'
require_contains "$verify_workflow" 'Test-SessionAdmissionScripts.ps1 -PostgresImage postgres:17-alpine'
require_contains "$workflow" 'Test-SessionAdmissionScripts.ps1 -PostgresImage postgres:17-alpine'
deploy_session_admission_test_line="$(grep -nF 'Test-SessionAdmissionScripts.ps1' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
deploy_image_publish_line="$(grep -nF 'name: Publish verified backend image' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
if (( deploy_session_admission_test_line >= deploy_image_publish_line )); then
  echo "Os scripts de pausa/retomada precisam passar antes da publicação da imagem backend." >&2
  exit 1
fi
if grep -Fq '\quit 3' "$repo_root/tools/Database/Pause-SessionAdmission.sql" "$repo_root/tools/Database/Resume-SessionAdmission.sql"; then
  echo "Scripts psql não podem usar '\quit 3', que não garante código de saída não zero." >&2
  exit 1
fi
require_contains "$repo_root/docs/deploy-preflight.sql" 'session_admission_open'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'ProductionDatabaseConfiguration.Validate'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Data/ProductionDatabaseConfiguration.cs" 'SslMode.VerifyFull'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/ProductionDatabaseConfigurationTests.cs" 'AcceptsExplicitPostgresTlsModes'
require_contains "$workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$workflow" '      - "tools/Database/**"'
require_contains "$verify_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$frontend_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$secret_scan_workflow" 'Scan-GitChangesForSecrets.sh'
require_contains "$secret_scan_workflow" '  push:'
require_contains "$secret_scan_workflow" '  pull_request:'
require_contains "$secret_scan_workflow" '  workflow_dispatch:'
require_contains "$verify_workflow" 'Verify migration impact and recovery review'
require_contains "$workflow" 'Verify migration impact and recovery review'
require_contains "$verify_workflow" 'python -B tools/Deployment/check_migration_risk.py'
require_contains "$workflow" 'python -B tools/Deployment/check_migration_risk.py'
require_contains "$verify_workflow" 'python -B tools/Deployment/test_bound_migration_lock_wait.py'
require_contains "$workflow" 'python -B tools/Deployment/test_bound_migration_lock_wait.py'
require_contains "$repo_root/tools/Deployment/test_check_migration_risk.py" 'test_detects_stale_hash_after_reviewed_migration_changes'
require_contains "$repo_root/docs/migration-risk-review.json" '20260730120000_AddMdbStatusAndAutomaticSession'
require_contains "$secret_scan_workflow" 'permissions:'
require_contains "$secret_scan_workflow" '  contents: read'
require_contains "$workflow" 'fetch-depth: 0'
require_contains "$verify_workflow" 'fetch-depth: 0'
require_contains "$frontend_workflow" 'fetch-depth: 0'
require_contains "$secret_scan_workflow" 'fetch-depth: 0'
require_contains "$secret_scan" 'zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f'
require_contains "$secret_scan" 'Determine-SecretScanRange.py'
require_contains "$secret_scan_range" 'merge-base'
require_contains "$secret_scan_range" 'return head_sha'
require_contains "$secret_scan_range_test" 'test_new_branch_scans_every_commit_since_default_branch'
require_contains "$secret_scan_range_test" 'test_new_branch_without_default_ref_scans_full_reachable_history'
for scan_workflow in "$workflow" "$verify_workflow" "$frontend_workflow" "$secret_scan_workflow"; do
  require_contains "$scan_workflow" 'SECRET_SCAN_DEFAULT_BRANCH:'
  require_contains "$scan_workflow" 'python3 tools/Deployment/test_secret_scan_range.py'
done
deploy_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
deploy_publish_line="$(grep -nF 'name: Publish verified backend image' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
deploy_frontend_compatibility_line="$(grep -nF -- '--frontend-ref "$frontend_ref"' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
verify_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$verify_workflow" | head -n 1 | cut -d: -f1)"
verify_test_line="$(grep -nF 'name: Run backend tests' "$verify_workflow" | head -n 1 | cut -d: -f1)"
verify_frontend_compatibility_line="$(grep -nF -- '--backend-ref "$backend_ref"' "$verify_workflow" | head -n 1 | cut -d: -f1)"
verify_frontend_tests_line="$(grep -nF '      - name: Run tests' "$verify_workflow" | head -n 1 | cut -d: -f1)"
frontend_scan_line="$(grep -nF 'Scan-GitChangesForSecrets.sh' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
frontend_compatibility_line="$(grep -nF -- '--backend-ref "$backend_ref"' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
frontend_publish_line="$(grep -nF 'name: Publish tested candidate image' "$frontend_workflow" | head -n 1 | cut -d: -f1)"
release_migration_test_line="$(grep -nF 'name: Apply generated PostgreSQL release segments to an isolated database' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
if (( deploy_scan_line >= deploy_publish_line || deploy_frontend_compatibility_line >= deploy_publish_line || verify_scan_line >= verify_test_line || frontend_scan_line >= frontend_publish_line || frontend_compatibility_line >= frontend_publish_line || verify_frontend_compatibility_line >= verify_frontend_tests_line )); then
  echo "Os scans e contratos frontend/API de destino devem passar antes dos testes/publicação pertinentes." >&2
  exit 1
fi
if [[ -z "$release_migration_test_line" ]] || (( release_migration_test_line >= deploy_publish_line )); then
  echo "Os segmentos SQL PostgreSQL devem ser aplicados em banco descartável antes de publicar a imagem da API." >&2
  exit 1
fi
require_contains "$temporary_dir/build-job" 'migrations has-pending-model-changes'
require_contains "$temporary_dir/build-job" 'before_session_index=20260715023221_AddTelemetrySessionManagement'
require_contains "$temporary_dir/build-job" 'session_index=20260730120000_AddMdbStatusAndAutomaticSession'
require_contains "$temporary_dir/build-job" 'before_payment_index=20260926173525_AddWebhookInbox'
require_contains "$temporary_dir/build-job" 'payment_index=20260926174359_AddPaymentReconciliationTracking'
require_contains "$temporary_dir/build-job" 'before_inventory_mapping_index=20261026083333_AddSessionProductSnapshot'
require_contains "$temporary_dir/build-job" 'inventory_mapping_index=20261026091223_IndexInventoryMappingMismatchAlerts'
require_contains "$temporary_dir/build-job" 'after_inventory_mapping_index=20261102215805_ClearLegacyTelemetryOwnerRoutingMetadata'
require_contains "$temporary_dir/build-job" '20261102215805_ClearLegacyTelemetryOwnerRoutingMetadata'
require_contains "$temporary_dir/build-job" 'NEW."CommandRoutingVersion" := NULL;'
require_contains "$temporary_dir/build-job" 'migrations script 0 "$before_session_index" --idempotent'
require_contains "$temporary_dir/build-job" 'migrations script "$before_session_index" "$session_index"'
require_contains "$temporary_dir/build-job" 'migrations script "$session_index" "$before_payment_index" --idempotent'
require_contains "$temporary_dir/build-job" 'migrations script "$before_payment_index" "$payment_index"'
require_contains "$temporary_dir/build-job" 'migrations script "$payment_index" "$before_inventory_mapping_index" --idempotent'
require_contains "$temporary_dir/build-job" 'migrations script "$before_inventory_mapping_index" "$inventory_mapping_index"'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-before-session-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migration-session-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-between-online-indexes.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migration-payment-reconciliation-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-after-payment-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migration-inventory-mapping-index.sql'
require_contains "$temporary_dir/build-job" '--output artifacts/postgresql-migrations-after-inventory-index.sql'
require_contains "$temporary_dir/build-job" 'name: Assemble SHA-matched release package'
require_contains "$temporary_dir/build-job" 'name: Verify isolated PostgreSQL migration rehearsal utility'
require_contains "$temporary_dir/build-job" './tools/Database/Test-PostgresMigrationRehearsal.ps1 -PostgresImage postgres:17-alpine'
require_contains "$temporary_dir/build-job" 'migration_scripts=(artifacts/postgresql-migration*.sql)'
require_contains "$temporary_dir/build-job" '[ "${#migration_scripts[@]}" -ne 7 ]'
require_contains "$temporary_dir/build-job" 'cp "${migration_scripts[@]}" "$package_dir/"'
require_contains "$temporary_dir/build-job" 'printf '\''%s\n'\'' "$GITHUB_SHA" > "$package_dir/source-sha.txt"'
require_contains "$temporary_dir/build-job" 'sha256sum -- *.sql *.md *.json *.txt *.ps1 > SHA256SUMS'
require_contains "$temporary_dir/build-job" 'test "$(cat "$package_dir/source-sha.txt")" = "$GITHUB_SHA"'
require_contains "$temporary_dir/build-job" 'sha256sum --check SHA256SUMS'
require_contains "$temporary_dir/build-job" 'name: Upload SHA-matched release package'
require_contains "$temporary_dir/build-job" 'name: release-package-${{ github.sha }}'
require_contains "$temporary_dir/build-job" 'package_dir=artifacts/release-package'
require_contains "$temporary_dir/build-job" 'path: artifacts/release-package/'
require_contains "$temporary_dir/build-job" 'cp tools/Database/List-UnresolvedTelemetryCommands.sql "$package_dir/"'
require_contains "$temporary_dir/build-job" 'cp tools/Database/Cancel-PendingTelemetryCommand.sql "$package_dir/"'
require_contains "$temporary_dir/build-job" 'cp tools/Database/Resolve-StaleProcessingTelemetryCommand.sql "$package_dir/"'
require_contains "$temporary_dir/build-job" 'cp tools/Database/Rehearse-PostgresMigrations.ps1 "$package_dir/"'
require_contains "$temporary_dir/build-job" 'cp docs/deploy-preflight.sql "$package_dir/"'
require_contains "$temporary_dir/build-job" 'cp docs/deploy-seguro.md "$package_dir/"'
require_contains "$temporary_dir/build-job" 'name: Bound PostgreSQL migration lock waits'
require_contains "$temporary_dir/build-job" 'name: Apply generated PostgreSQL release segments to an isolated database'
require_contains "$temporary_dir/build-job" './tools/Database/Test-PostgresReleaseMigrationSegments.ps1 -SegmentsDirectory artifacts -PostgresImage postgres:17-alpine'
require_contains "$temporary_dir/build-job" 'name: Build candidate backend image without publishing'
require_contains "$temporary_dir/build-job" 'load: true'
require_contains "$temporary_dir/build-job" 'name: Verify backend runtime image before publishing'
require_contains "$temporary_dir/build-job" 'bash tools/Deployment/Test-BackendRuntimeImage.sh "$BACKEND_IMAGE_TAG"'
require_contains "$temporary_dir/build-job" 'name: Verify backend Production startup with isolated PostgreSQL'
require_contains "$temporary_dir/build-job" './tools/Database/Test-BackendImagePostgresSmoke.ps1 -Image $env:BACKEND_IMAGE_TAG -SegmentsDirectory artifacts'
require_contains "$temporary_dir/build-job" 'name: Verify previous API overlap while a legacy MDB socket stays connected'
require_contains "$temporary_dir/build-job" 'Test-PreviousBackendRollbackCompatibility.py'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'overlap_socket_preserved=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_readiness=Ready_with_healthy_legacy_owner_relay'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_legacy_command_relay=acknowledged'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'legacy_socket_survives_candidate_stop=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'legacy_socket_survives_candidate_restart=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'legacy_api_ignores_candidate_deferred_outbox=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_financial_outbox_processed_during_overlap=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_post_restart_command_relay=acknowledged'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_legacy_session_start=accepted'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_legacy_mdb_status=correlated'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_legacy_session_cancel=physically_closed'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_readiness_blocks_unavailable_legacy_owner=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'candidate_legacy_command_after_owner_loss=failed_closed'
require_contains "$repo_root/tools/Deployment/Validate-TelemetryOverlapReadiness.py" 'overlap_runtime_preflight=passed'
require_contains "$repo_root/tools/Deployment/Validate-TelemetryOverlapReadiness.py" 'minimum-legacy-connections'
require_contains "$repo_root/tools/Deployment/Validate-TelemetryOverlapReadiness.py" 'candidate_unroutable_connections=0'
require_contains "$repo_root/tools/Deployment/Validate-TelemetryOverlapReadiness.py" 'VENDMACHINES_ADMIN_BEARER_TOKEN'
require_contains "$repo_root/tools/Deployment/Validate-TelemetryOverlapReadiness.py" 'class RejectRedirects'
require_contains "$repo_root/tools/Deployment/test_validate_telemetry_overlap_readiness.py" 'test_accepts_healthy_relay_and_matching_legacy_connection_metrics'
require_contains "$repo_root/tools/Deployment/test_validate_telemetry_overlap_readiness.py" 'test_blocks_when_no_legacy_machine_connection_is_observed'
require_contains "$repo_root/tools/Deployment/test_validate_telemetry_overlap_readiness.py" 'test_does_not_follow_redirects'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'legacy_payment_webhook_routed_to_owner=yes'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'legacy_payment_webhook_candidate_inbox=empty'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/SessionOrchestrator.cs" 'deferred_legacy_overlap'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/SessionOrchestratorFlowTests.cs" 'LegacyApiOverlap_ProcessesCandidateOwnedDeferredOutboxWithoutCompetingWithLegacyWorker'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/SessionOrchestratorFlowTests.cs" 'LegacyApiOverlap_KeepsFailedCandidateOutboxInLegacyIgnoredStatusForRetry'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/SessionOrchestratorFlowTests.cs" 'LegacyApiOverlap_ReclaimsExpiredOutboxLeaseWithoutExposingItToLegacyWorker'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/SessionOrchestratorFlowTests.cs" 'DeferredFinancialOutbox_ResumesAndCompletesAfterLegacyRelayIsRemoved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Controllers/MachinesController.cs" 'CanSafelyChangeOnlineMachineCredentialAsync'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/MachineCredentialControllerTests.cs" 'CredentialDoesNotMutateWhenOnlineOwnerCannotBeRouted'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Controllers/WorkerHealthController.cs" 'oldestDeferredLegacyFinancialOutboxAgeSeconds'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Controllers/WorkerHealthController.cs" 'legacyRelayEligibleCount'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/BackgroundWorkerHealthRegistryTests.cs" 'GetProperty("legacyRelay")'
if [[ -f "$repo_root/apps/frontend/src/test/Admin.test.tsx" ]]; then
  require_contains "$repo_root/apps/frontend/src/pages/Admin.tsx" 'No processo anterior'
  require_contains "$repo_root/apps/frontend/src/test/Admin.test.tsx" 'shows healthy legacy sockets separately from release-blocking routes'
fi
require_contains "$repo_root/apps/backend/VendingMachines.Api/Controllers/LegacyMachineConnectionsController.cs" 'Authorize(Roles = AppRoles.PlatformAdmin)'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/LegacyMachineConnectionsControllerTests.cs" 'EndpointIsRestrictedToPlatformAdministrators'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/SessionOrchestratorFlowTests.cs" 'ExpiredPaymentDeadlineForLegacyOnlineOwnerIsLeftToPreviousApi'
if [[ -f "$repo_root/apps/frontend/src/test/Admin.test.tsx" ]]; then
  require_contains "$repo_root/apps/frontend/src/pages/Admin.tsx" 'Máquinas ainda conectadas à API anterior'
  require_contains "$repo_root/apps/frontend/src/test/Admin.test.tsx" 'lists safe machine details and paginates legacy connections'
fi
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/BackgroundWorkerHealthRegistryTests.cs" 'deferred_legacy_overlap'
if [[ -f "$repo_root/apps/frontend/src/test/Admin.test.tsx" ]]; then
  require_contains "$repo_root/apps/frontend/src/pages/Admin.tsx" 'Adiadas na sobreposição'
  require_contains "$repo_root/apps/frontend/src/test/Admin.test.tsx" 'surfaces deferred financial operations and their age during legacy overlap'
fi
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/LegacyTelemetryRequestRelay.cs" 'RelayHopHeaderName'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Middlewares/LegacyRelayLoopGuardMiddleware.cs" 'legacy_relay_loop_blocked'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'LegacyRelayLoopGuardMiddleware'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/LegacyRelayLoopGuardMiddlewareTests.cs" 'RelayHopReturningToCandidateIsRejectedBeforeEndpointExecution'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/LegacyTelemetryRequestRelayTests.cs" 'SelfReferentialOwnerEndpointIsNotConsideredHealthy'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'Telemetry__LegacyApiBaseUrl=http://previous-api:8080'
require_contains "$repo_root/tools/Database/Test-PreviousBackendRollbackCompatibility.py" 'command_payload.get("status") != "acknowledged"'
require_contains "$temporary_dir/build-job" 'name: Publish verified backend image'
require_contains "$temporary_dir/build-job" 'docker push "$image_tag"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" '"network", "create", "--driver", "bridge", "--internal", $networkName'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'Local smoke bootstrap account'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" '"reason"\s*:\s*"legacy_owner_api_unavailable"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'A admissão pode permanecer aberta, mas o readiness precisa bloquear a conexão legada sem relay privado saudável'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'machine_connections_unroutable'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'legacy_owner_api_unavailable'
require_contains "$repo_root/tools/Deployment/Test-Coolify-Deployment.sh" 'preflight-machine-connections-unroutable'
require_contains "$repo_root/tools/Deployment/Test-Coolify-Deployment.sh" 'deploy-machine-connections-unroutable'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'unroutable_connections)'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" '"status"\s*:\s*"Ready"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'SELECT "NewSessionsPaused" FROM "SessionAdmissionGates"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" '$admissionPaused -ne "f"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" '$routedActiveSessionResponse'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'active_session_without_routable_machine'
if grep -Fq 'SET "NewSessionsPaused" = TRUE' "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1"; then
  echo "O smoke de readiness não pode pausar novas sessões para liberar o deploy." >&2
  exit 1
fi
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'CREATE DATABASE vendmachines_pending'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'pendingApiStatus -ne "exited"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'pendingHistoryState -ne "t"'
require_contains "$repo_root/tools/Database/Test-BackendImagePostgresSmoke.ps1" 'Nenhuma porta foi publicada e a rede Docker era interna.'
require_contains "$verify_workflow" 'tools/Deployment/Test-BackendRuntimeImage.sh vend-machines-backend:verify'
require_contains "$repo_root/tools/Deployment/Test-BackendRuntimeImage.sh" '--network none'
require_contains "$repo_root/tools/Deployment/Test-BackendRuntimeImage.sh" 'appsettings.*.json'
require_contains "$repo_root/tools/Deployment/Test-BackendRuntimeImage.sh" 'A configuração Encryption:Key é obrigatória fora do ambiente de desenvolvimento.'
require_contains "$temporary_dir/build-job" 'name: Audit frontend dependencies'
require_contains "$temporary_dir/build-job" 'name: Check frontend types'
require_contains "$temporary_dir/build-job" 'name: Lint frontend'
require_contains "$temporary_dir/build-job" 'name: Test frontend'
require_contains "$temporary_dir/build-job" 'name: Run browser smoke tests with isolated demo data'
require_contains "$temporary_dir/build-job" 'name: Build frontend'
require_contains "$temporary_dir/build-job" 'SET lock_timeout = '\''5s'\'';'
require_contains "$temporary_dir/build-job" 'tools/Deployment/bound_migration_lock_wait.py artifacts'
require_contains "$temporary_dir/build-job" 'tools/Deployment/test_bound_migration_lock_wait.py'
require_contains "$temporary_dir/build-job" '20261025150422_AddTelemetryCommandRoutingCapability'
require_contains "$temporary_dir/build-job" 'ALTER TABLE "MachineConnectionStates" ADD "CommandRoutingVersion" integer;'
require_contains "$temporary_dir/build-job" '20261026091223_IndexInventoryMappingMismatchAlerts'
require_contains "$temporary_dir/build-job" 'mapping_script = scripts["inventory_mapping"]'
require_contains "$temporary_dir/build-job" '"indisvalid", "indisready", "pg_get_indexdef", "pg_get_expr", "SaleMappingMismatch", "RAISE EXCEPTION"'
require_contains "$temporary_dir/build-job" 'mapping_script.index("CREATE INDEX CONCURRENTLY") > mapping_script.index("START TRANSACTION")'
require_contains "$temporary_dir/build-job" 'name: Verify online migration segments are outside EF idempotency wrappers'
segment_validation_line="$(grep -nF 'name: Verify online migration segments are outside EF idempotency wrappers' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
lock_timeout_line="$(grep -nF 'name: Bound PostgreSQL migration lock waits' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
package_assembly_line="$(grep -nF 'name: Assemble SHA-matched release package' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
package_checksum_line="$(grep -nF 'sha256sum --check SHA256SUMS' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
package_upload_line="$(grep -nF 'name: Upload SHA-matched release package' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
image_build_line="$(grep -nF 'name: Build candidate backend image without publishing' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
image_validation_line="$(grep -nF 'name: Verify backend runtime image before publishing' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
image_startup_line="$(grep -nF 'name: Verify backend Production startup with isolated PostgreSQL' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
image_overlap_line="$(grep -nF 'name: Verify previous API overlap while a legacy MDB socket stays connected' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
image_publish_line="$(grep -nF 'name: Publish verified backend image' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_audit_line="$(grep -nF 'name: Audit frontend dependencies' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_types_line="$(grep -nF 'name: Check frontend types' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_lint_line="$(grep -nF 'name: Lint frontend' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_tests_line="$(grep -nF 'name: Test frontend' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_e2e_line="$(grep -nF 'name: Run browser smoke tests with isolated demo data' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
frontend_build_line="$(grep -nF 'name: Build frontend' "$temporary_dir/build-job" | head -n 1 | cut -d: -f1)"
if (( lock_timeout_line >= segment_validation_line || segment_validation_line >= package_assembly_line )); then
  echo "As migrations precisam receber limite de lock e passar nos gates antes de montar o pacote de release." >&2
  exit 1
fi
if (( package_assembly_line >= package_checksum_line || package_checksum_line >= image_publish_line || image_publish_line >= package_upload_line )); then
  echo "Artefatos só podem ser enviados após validações, checagem do pacote e publicação bem-sucedida da imagem." >&2
  exit 1
fi
if (( image_build_line >= image_validation_line || image_validation_line >= image_startup_line || image_startup_line >= image_overlap_line || image_overlap_line >= image_publish_line )); then
  echo "A imagem backend precisa passar pelos smoke tests de runtime, PostgreSQL e sobreposição com socket legado antes do push." >&2
  exit 1
fi
if (( frontend_audit_line >= frontend_types_line || frontend_types_line >= frontend_lint_line || frontend_lint_line >= frontend_tests_line || frontend_tests_line >= frontend_e2e_line || frontend_e2e_line >= frontend_build_line || frontend_build_line >= image_publish_line )); then
  echo "A imagem só pode ser publicada após auditoria, tipos, lint, testes, smoke de navegador e build do frontend." >&2
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
  "inputs.platform_fee_settings_validated == 'yes'" \
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
require_contains "$repo_root/.github/workflows/deploy.yml" 'EXPECTED_API_REVISION: ${{ github.sha }}'
require_contains "$repo_root/.github/workflows/deploy.yml" 'ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT:'
require_contains "$repo_root/.github/workflows/deploy.yml" 'bootstrap_deploy_readiness_endpoint:'
require_contains "$repo_root/.github/workflows/deploy.yml" "inputs.bootstrap_deploy_readiness_endpoint == 'yes'"
require_contains "$repo_root/.github/workflows/deploy.yml" 'bootstrap_api_readiness_endpoint:'
require_contains "$repo_root/.github/workflows/deploy.yml" "inputs.bootstrap_api_readiness_endpoint == 'yes'"
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'ALLOW_MISSING_API_READINESS_ENDPOINT'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'validate_legacy_api_liveness'
if (( $(grep -cF 'ALLOW_MISSING_API_READINESS_ENDPOINT: ${{ inputs.bootstrap_api_readiness_endpoint' "$temporary_dir/release-job") != 2 )); then
  echo "A autorização de bootstrap da API deve ser passada ao preflight e à revalidação antes da troca." >&2
  exit 1
fi
api_readiness_bootstrap_input="$(sed -n '/^      bootstrap_api_readiness_endpoint:/,/^      machine_connections_preserved:/p' "$workflow")"
if ! printf '%s\n' "$api_readiness_bootstrap_input" | grep -Fq 'default: "no"' ||
   ! printf '%s\n' "$api_readiness_bootstrap_input" | grep -Fq 'required: true'; then
  echo "O bootstrap de /health/ready deve ser explícito, obrigatório e começar desligado." >&2
  exit 1
fi
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
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'device_messages_unresolved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'DeviceMessageInbox'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'payment_webhooks_unresolved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'WebhookInboxMessages'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'financial_outbox_in_flight'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'OutboxMessages'
require_contains "$repo_root/docs/deploy-preflight.sql" 'unprocessed_device_messages'
require_contains "$repo_root/docs/deploy-preflight.sql" 'unprocessed_payment_webhooks'
require_contains "$repo_root/docs/deploy-preflight.sql" 'financial_outbox_in_flight'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'unprocessed_device_messages_block_resume'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'unprocessed_payment_webhooks_block_resume'
require_contains "$repo_root/tools/Database/Resume-SessionAdmission.sql" 'financial_outbox_in_flight_blocks_resume'
require_contains "$repo_root/tools/Deployment/Coolify-Deployment.sh" 'device_messages_unresolved'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'command.Error == "ack_timeout"'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/DeploymentReadinessService.cs" 'dispatch.Error == "ack_timeout"'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentReadinessServiceTests.cs" 'DeploymentPreflightAllowsOpenSalesWhenConnectionsAreRoutableAndBlocksUnresolvedOperations'
if (( $(grep -cF 'validate_current_api_readiness' "$repo_root/tools/Deployment/Coolify-Deployment.sh") < 3 )) ||
   ! grep -Fq 'validate_production_deploy_readiness "$response_file" || return 1' "$repo_root/tools/Deployment/Coolify-Deployment.sh"; then
  echo "O gate de atividade operacional precisa existir e ser consultado antes da promoção e antes do webhook." >&2
  exit 1
fi
last_deploy_preflight_line="$(grep -nF 'validate_current_api_readiness "$response_dir/deploy-ready.json"' "$repo_root/tools/Deployment/Coolify-Deployment.sh" | tail -n 1 | cut -d: -f1)"
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
require_contains "$repo_root/apps/backend/VendingMachines.Api/Program.cs" 'ShutdownTimeout = TimeSpan.FromSeconds(GracefulShutdownBudget.HostTimeoutSeconds)'
require_contains "$repo_root/apps/backend/VendingMachines.Api/Services/GracefulShutdownBudget.cs" 'public const int HostTimeoutSeconds = 45;'
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
require_contains "$workflow" 'platform_fee_settings_validated:'
if grep -Eq 'active_vend_sessions_drained|legacy_manual_commands_paused' "$workflow"; then
  echo "A release não pode depender de pausar vendas ou comandos enquanto exige continuidade operacional." >&2
  exit 1
fi
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentReadinessServiceTests.cs" 'DeploymentPreflightAllowsOpenSalesWhenConnectionsAreRoutableAndBlocksUnresolvedOperations'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentReadinessEndpointTests.cs" 'DeploymentReadinessDoesNotRequirePausingNewSessionAdmission'
fee_gate_description="$(sed -n '/^      platform_fee_settings_validated:/,/^      bootstrap_deploy_readiness_endpoint:/p' "$workflow")"
if ! grep -Fq 'default: "no"' <<<"$fee_gate_description" ||
   ! grep -Fq 'invalid_platform_fee_settings = 0' <<<"$fee_gate_description"; then
  echo "A release precisa começar bloqueada até confirmar taxa global válida no preflight." >&2
  exit 1
fi
require_contains "$repo_root/docs/deploy-seguro.md" 'docs/deploy-preflight.sql'
require_contains "$repo_root/docs/deploy-seguro.md" 'platform_fee_settings_validated=yes'
require_contains "$repo_root/docs/deploy-preflight.sql" 'BEGIN TRANSACTION READ ONLY;'
require_contains "$repo_root/docs/deploy-preflight.sql" "SET LOCAL statement_timeout = '5s';"
require_contains "$repo_root/docs/deploy-preflight.sql" 'AS unresolved_machine_commands'
require_contains "$repo_root/docs/deploy-preflight.sql" 'AS unresolved_session_closures'
require_contains "$repo_root/docs/deploy-preflight.sql" 'AS invalid_platform_fee_settings'
require_contains "$repo_root/docs/deploy-preflight.sql" "dispatch.\"Status\" = 'Indeterminate'"
require_contains "$repo_root/docs/deploy-preflight.sql" "dispatch.\"Operation\" <> 'MdbStatus'"
require_contains "$repo_root/docs/deploy-preflight.sql" "\"Error\" IN ('ack_timeout', 'command_outcome_unknown')"
require_contains "$repo_root/tools/Database/Resolve-TelemetryCommand.sql" "dispatch.\"Status\" = 'Failed'"
require_contains "$repo_root/docs/deploy-preflight.sql" 'TelemetryCommandResolutions'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentPreflightSqlTests.cs" "'ack_timeout'"
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/DeploymentPreflightSqlTests.cs" 'ReadOnlyPreflightCountsOpenSalesAndUnresolvedMachineCommands'
require_contains "$repo_root/apps/backend/VendingMachines.Api.Tests/VendingMachines.Api.Tests.csproj" 'deploy-preflight.sql'
require_contains "$repo_root/tools/Deployment/Test-Coolify-Deployment.sh" 'preflight-indeterminate-command'
require_contains "$repo_root/tools/Deployment/Test-Coolify-Deployment.sh" 'deploy-indeterminate-command'
require_contains "$temporary_dir/release-job" 'inputs.continuity_test_evidence_url != '\''none'\'''
require_contains "$workflow" 'Validate telemetry continuity evidence link'
gate_description="$(sed -n '/^      machine_connections_preserved:/,/^      continuity_test_evidence_url:/p' "$workflow")"
if ! grep -Fq 'default: "no"' <<<"$gate_description" ||
   ! grep -Fq 'sem fechar/reconectar sockets ativos nem pausar novas sessões' <<<"$gate_description" ||
   ! grep -Fq 'Qualquer EndpointUnavailable, reconexão ou pausa operacional reprova este critério.' <<<"$gate_description"; then
  echo "O gate deve começar bloqueado e rejeitar desconexões, reconexões e pausas de operação." >&2
  exit 1
fi
evidence_description="$(awk '/^      continuity_test_evidence_url:/{in_section=1; next} in_section && /^      [a-z_]+:/{exit} in_section{print}' "$workflow")"
if ! grep -Fq 'default: "none"' <<<"$evidence_description"; then
  echo "O link de evidência de continuidade deve começar vazio/bloqueado." >&2
  exit 1
fi
require_contains "$workflow" 'GITHUB_REPOSITORY: ${{ github.repository }}'
GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/example/repository/actions/runs/123" >/dev/null
GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/example/repository/pull/123" >/dev/null
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "http://github.com/example/repository/actions/runs/123" >/dev/null 2>&1; then
  echo "O validador aceitou evidência de continuidade em HTTP sem TLS." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://example.com/example/repository/actions/runs/123" >/dev/null 2>&1; then
  echo "O validador aceitou um host externo ao GitHub como evidência." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/other/repository/actions/runs/123" >/dev/null 2>&1; then
  echo "O validador aceitou evidência de outro repositório." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://user:password@github.com/example/repository/pull/123" >/dev/null 2>&1; then
  echo "O validador aceitou credenciais embutidas no link de evidência." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/example/repository/pull/123?redirect=https://evil.invalid" >/dev/null 2>&1; then
  echo "O validador aceitou query string no link de evidência." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://[invalid/report" >/dev/null 2>&1; then
  echo "O validador aceitou um link malformado como evidência." >&2
  exit 1
fi
if GITHUB_REPOSITORY=example/repository python3 "$repo_root/tools/Deployment/Validate-ContinuityEvidence.py" "https://github.com/example/repository/issues/123" >/dev/null 2>&1; then
  echo "O validador aceitou uma rota que não identifica PR ou execução." >&2
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
  'npx playwright install --with-deps chromium firefox webkit' \
  'npm run test:e2e:cross-browser' \
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
