#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
temporary_dir="$(mktemp -d)"
cleanup() {
  rm -f "$temporary_dir/curl" "$temporary_dir/status-count" "$temporary_dir/health-count" \
    "$temporary_dir/preflight.log" "$temporary_dir/success.log" "$temporary_dir/failure.log" \
    "$temporary_dir/health-failure.log" "$temporary_dir/health-body-failure.log" \
    "$temporary_dir/preflight-health-failure.log" "$temporary_dir/preflight-health-wrong-body.log" \
    "$temporary_dir/preflight-active-session.log" "$temporary_dir/preflight-command-in-flight.log" \
    "$temporary_dir/bootstrap-unconfirmed.log" "$temporary_dir/bootstrap-preflight.log" "$temporary_dir/bootstrap-deploy.log" \
    "$temporary_dir/bootstrap-still-missing.log" \
    "$temporary_dir/rolling-missing.log" \
    "$temporary_dir/unknown.log" "$temporary_dir/invalid-url.log" \
    "$temporary_dir/rolling-port.log" "$temporary_dir/rolling-health.log" \
    "$temporary_dir/rolling-compose.log" "$temporary_dir/rolling-fixed-name.log" \
    "$temporary_dir/rolling-fixed-ip.log" "$temporary_dir/rolling-grace.log" \
    "$temporary_dir/rolling-grace-string.log" "$temporary_dir/rolling-grace-bool.log" \
    "$temporary_dir/rolling-auto-deploy.log" "$temporary_dir/rolling-auto-deploy-missing.log" \
    "$temporary_dir/rolling-git-buildpack.log" "$temporary_dir/rolling-wrong-image.log" "$temporary_dir/rolling-wrong-image-tag.log" \
    "$temporary_dir/rolling-wrong-domain.log" "$temporary_dir/deploy-revalidation.log" \
    "$temporary_dir/deploy-active-session.log" \
    "$temporary_dir/runtime-missing.log" "$temporary_dir/runtime-placeholder.log" \
    "$temporary_dir/runtime-masked.log" \
    "$temporary_dir/runtime-email-incomplete.log" "$temporary_dir/runtime-email-valid.log" \
    "$temporary_dir/runtime-email-disabled.log" \
    "$temporary_dir/runtime-email-placeholder.log" \
    "$temporary_dir/runtime-db-no-tls.log" \
    "$temporary_dir/trigger-count" "$temporary_dir/deploy-ready-count" "$temporary_dir/wrong-target.log"
  rmdir "$temporary_dir"
}
trap cleanup EXIT

cat > "$temporary_dir/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
method="GET"
output_file="/dev/null"
url=""
while (($#)); do
  case "$1" in
    -o|--output) output_file="$2"; shift 2 ;;
    -w) shift 2 ;;
    -X|--request) method="$2"; shift 2 ;;
    -H|--header|--max-time) shift 2 ;;
    -sS|--silent|--show-error) shift ;;
    *) url="$1"; shift ;;
  esac
done

http_status="200"
body="{}"
if [[ "$method" == "POST" && "$url" == */api/v1/deploy\?* ]]; then
  trigger_count=0
  [[ ! -f "$COOLIFY_TEST_TRIGGER_COUNT_FILE" ]] || trigger_count="$(cat "$COOLIFY_TEST_TRIGGER_COUNT_FILE")"
  trigger_count=$((trigger_count + 1))
  printf '%s' "$trigger_count" > "$COOLIFY_TEST_TRIGGER_COUNT_FILE"
  if [[ "${COOLIFY_TEST_MODE:-success}" == "wrong-target" ]]; then
    body='{"deployments":[{"deployment_uuid":"deployment-test-123","resource_uuid":"another-app"}]}'
  else
    body='{"deployments":[{"deployment_uuid":"deployment-test-123","resource_uuid":"app-test"}]}'
  fi
elif [[ "$url" == */api/v1/applications/app-test ]]; then
  case "${COOLIFY_TEST_MODE:-success}" in
    rolling-port) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"8080:8080","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-health) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":false,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-compose) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockercompose","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-git-buildpack) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerfile","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-fixed-name) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":true,"stop_grace_period":60}}' ;;
    rolling-fixed-ip) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","custom_docker_run_options":"--ip 10.0.0.10","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-grace) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":59}}' ;;
    rolling-grace-string) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":"60"}}' ;;
    rolling-grace-bool) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":true}}' ;;
    rolling-wrong-domain) body='{"uuid":"app-test","fqdn":"https://staging.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-wrong-image) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/other-image","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-wrong-image-tag) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"stable","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-auto-deploy) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":true,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    rolling-auto-deploy-missing) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
    *) body='{"uuid":"app-test","fqdn":"https://api.example.com","build_pack":"dockerimage","docker_registry_image_name":"ghcr.io/example/vend-machines-backend","docker_registry_image_tag":"latest","ports_mappings":"","health_check_enabled":true,"health_check_path":"/health/ready","health_check_port":"8080","health_check_method":"GET","health_check_return_code":200,"settings":{"is_auto_deploy_enabled":false,"is_consistent_container_name_enabled":false,"stop_grace_period":60}}' ;;
  esac
elif [[ "$url" == */api/v1/applications/app-test/envs ]]; then
  case "${COOLIFY_TEST_MODE:-success}" in
    runtime-missing) body='[]' ;;
    runtime-db-no-tls) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true}]' ;;
    runtime-email-incomplete) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true},{"key":"AccountEmail__Enabled","value":"true","is_runtime":true}]' ;;
    runtime-email-disabled) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true},{"key":"AccountEmail__Enabled","value":"false","is_runtime":true},{"key":"AccountEmail__Host","value":"smtp.test.internal","is_runtime":true},{"key":"AccountEmail__FromAddress","value":"noreply@vendmachine.test","is_runtime":true},{"key":"AccountEmail__PublicBaseUrl","value":"https://app.vendmachine.test","is_runtime":true}]' ;;
    runtime-email-valid) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true},{"key":"AccountEmail__Enabled","value":"true","is_runtime":true},{"key":"AccountEmail__Host","value":"smtp.test.internal","is_runtime":true},{"key":"AccountEmail__FromAddress","value":"noreply@vendmachine.test","is_runtime":true},{"key":"AccountEmail__PublicBaseUrl","value":"https://app.vendmachine.test","is_runtime":true},{"key":"AccountEmail__Username","value":"synthetic-smtp-user","is_runtime":true},{"key":"AccountEmail__Password","value":"synthetic-smtp-password","is_runtime":true}]' ;;
    runtime-masked) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"********","is_runtime":true},{"key":"Encryption__LegacyKey","value":"********","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true}]' ;;
    runtime-placeholder) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=REPLACE_WITH_PASSWORD;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true}]' ;;
    *) body='[{"key":"ConnectionStrings__DefaultConnection","value":"Host=db.test.internal;Database=vending;Username=app;Password=synthetic-db-password;SSL Mode=Require","is_runtime":true},{"key":"Jwt__Key","value":"SYNTHETIC_SECRET_CANARY_12345678901234567890123456789012","is_runtime":true},{"key":"Encryption__Key","value":"synthetic-encryption-key","is_runtime":true},{"key":"Encryption__LegacyKey","value":"synthetic-encryption-key","is_runtime":true},{"key":"MercadoPago__ClientSecret","value":"synthetic-client-secret","is_runtime":true},{"key":"MercadoPago__WebhookSecret","value":"synthetic-webhook-secret","is_runtime":true},{"key":"AccountEmail__Enabled","value":"true","is_runtime":true},{"key":"AccountEmail__Host","value":"smtp.test.internal","is_runtime":true},{"key":"AccountEmail__FromAddress","value":"noreply@vendmachine.test","is_runtime":true},{"key":"AccountEmail__PublicBaseUrl","value":"https://app.vendmachine.test","is_runtime":true}]' ;;
  esac
  if [[ "${COOLIFY_TEST_MODE:-success}" == "runtime-email-placeholder" ]]; then
    body="${body//smtp.test.internal/smtp.example.com}"
    body="${body//app.vendmachine.test/app.example.com}"
  fi
elif [[ "$url" == */api/v1/deployments ]]; then
  body='[]'
elif [[ "$url" == */api/v1/deployments/deployment-test-123 ]]; then
  status_count=0
  [[ ! -f "$COOLIFY_TEST_STATUS_COUNT_FILE" ]] || status_count="$(cat "$COOLIFY_TEST_STATUS_COUNT_FILE")"
  status_count=$((status_count + 1))
  printf '%s' "$status_count" > "$COOLIFY_TEST_STATUS_COUNT_FILE"
  case "${COOLIFY_TEST_MODE:-success}:$status_count" in
    failed:*) body='{"status":"failed"}' ;;
    unknown:*) body='{"status":"unexpected"}' ;;
    rolling-missing:*) body='{"status":"finished","logs":"Pulling image; container replacement started"}' ;;
    success:1) body='{"status":"queued","logs":"Rolling update started"}' ;;
    success:2) body='{"status":"in_progress","logs":"Rolling update started; New container is healthy"}' ;;
    *) body='{"status":"finished","logs":"Rolling update started; New container is healthy; Rolling update completed"}' ;;
  esac
elif [[ "$url" == */health/deploy-ready ]]; then
  case "${COOLIFY_TEST_MODE:-success}" in
    bootstrap-old-api)
      deploy_ready_count=0
      [[ ! -f "$COOLIFY_TEST_DEPLOY_READY_COUNT_FILE" ]] || deploy_ready_count="$(cat "$COOLIFY_TEST_DEPLOY_READY_COUNT_FILE")"
      deploy_ready_count=$((deploy_ready_count + 1))
      printf '%s' "$deploy_ready_count" > "$COOLIFY_TEST_DEPLOY_READY_COUNT_FILE"
      if [[ "$deploy_ready_count" == "1" ]]; then
        http_status="404"
        body='{"status":"NotFound"}'
      else
        body='{"status":"Ready"}'
      fi
      ;;
    bootstrap-missing-unconfirmed) http_status="404"; body='{"status":"NotFound"}' ;;
    bootstrap-still-missing) http_status="404"; body='{"status":"NotFound"}' ;;
    preflight-active-session|deploy-active-session) http_status="503"; body='{"status":"NotReady","reason":"active_vend_sessions"}' ;;
    preflight-command-in-flight) http_status="503"; body='{"status":"NotReady","reason":"telemetry_commands_in_flight"}' ;;
    *) body='{"status":"Ready"}' ;;
  esac
elif [[ "$url" == */health/ready ]]; then
  health_count=0
  [[ ! -f "$COOLIFY_TEST_HEALTH_COUNT_FILE" ]] || health_count="$(cat "$COOLIFY_TEST_HEALTH_COUNT_FILE")"
  health_count=$((health_count + 1))
  printf '%s' "$health_count" > "$COOLIFY_TEST_HEALTH_COUNT_FILE"
  if [[ "${COOLIFY_DEPLOYMENT_STAGE:-}" == "preflight" ]]; then
    if [[ "${COOLIFY_TEST_MODE:-success}" == "preflight-health-failure" ]]; then
      http_status="503"
      body='{"status":"NotReady"}'
    elif [[ "${COOLIFY_TEST_MODE:-success}" == "preflight-health-wrong-body" ]]; then
      body='{"status":"NotReady"}'
    else
      body='{"status":"Ready"}'
    fi
  elif [[ "${COOLIFY_TEST_MODE:-success}" == "health-failure" || "$health_count" == "1" ]]; then
    http_status="503"
    body='{"status":"NotReady"}'
  elif [[ "${COOLIFY_TEST_MODE:-success}" == "health-wrong-body" ]]; then
    body='{"status":"NotReady"}'
  else
    body='{"status":"Ready"}'
  fi
else
  http_status="404"
fi

if [[ "$output_file" != "/dev/null" ]]; then
  printf '%s' "$body" > "$output_file"
fi
printf '%s' "$http_status"
CURL
chmod +x "$temporary_dir/curl"

export PATH="$temporary_dir:$PATH"
export COOLIFY_DEPLOY_WEBHOOK="https://coolify.example/api/v1/deploy?uuid=app-test&force=false"
export COOLIFY_API_TOKEN="test-only-token"
export PRODUCTION_API_HEALTH_URL="https://api.example.com/health/ready"
export PRODUCTION_API_DEPLOY_READY_URL="https://api.example.com/health/deploy-ready"
export REGISTRY="ghcr.io"
export IMAGE_NAME="example/vend-machines-backend"
export COOLIFY_TEST_STATUS_COUNT_FILE="$temporary_dir/status-count"
export COOLIFY_TEST_HEALTH_COUNT_FILE="$temporary_dir/health-count"
export COOLIFY_TEST_TRIGGER_COUNT_FILE="$temporary_dir/trigger-count"
export COOLIFY_TEST_DEPLOY_READY_COUNT_FILE="$temporary_dir/deploy-ready-count"
export COOLIFY_DEPLOYMENT_STAGE=preflight
export COOLIFY_DEPLOY_TIMEOUT_SECONDS=2
export COOLIFY_DEPLOY_POLL_INTERVAL_SECONDS=0
export POST_DEPLOY_HEALTH_TIMEOUT_SECONDS=2
export POST_DEPLOY_HEALTH_INTERVAL_SECONDS=0

bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/preflight.log"
grep -q "Readiness atual, ausência de venda/comando em curso, configuração de continuidade e acesso de leitura do Coolify validados" "$temporary_dir/preflight.log"
grep -q "rolling update e health check Ready validada" "$temporary_dir/preflight.log"

export COOLIFY_TEST_MODE=runtime-email-incomplete
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-email-incomplete.log" 2>&1; then
  echo "O teste esperava bloquear SMTP habilitado sem configurações obrigatórias." >&2
  exit 1
fi
grep -q "SMTP habilitado sem configuração obrigatória" "$temporary_dir/runtime-email-incomplete.log"

export COOLIFY_TEST_MODE=runtime-email-disabled
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-email-disabled.log" 2>&1; then
  echo "O teste esperava bloquear a release com confirmação de e-mail desativada." >&2
  exit 1
fi
grep -q "AccountEmail__Enabled precisa ser true" "$temporary_dir/runtime-email-disabled.log"

export COOLIFY_TEST_MODE=runtime-email-placeholder
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-email-placeholder.log" 2>&1; then
  echo "O teste esperava bloquear placeholders na configuração SMTP." >&2
  exit 1
fi
grep -q "SMTP habilitado com configuração de exemplo" "$temporary_dir/runtime-email-placeholder.log"

export COOLIFY_TEST_MODE=runtime-email-valid
bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-email-valid.log"
grep -q "Variáveis de runtime obrigatórias presentes" "$temporary_dir/runtime-email-valid.log"

export COOLIFY_TEST_MODE=runtime-missing
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-missing.log" 2>&1; then
  echo "O teste esperava bloquear a release sem segredos de runtime." >&2
  exit 1
fi
grep -q "Variáveis de runtime obrigatórias ausentes" "$temporary_dir/runtime-missing.log"

export COOLIFY_TEST_MODE=runtime-db-no-tls
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-db-no-tls.log" 2>&1; then
  echo "O teste esperava bloquear a conexão PostgreSQL sem TLS explícito." >&2
  exit 1
fi
grep -q "precisa exigir TLS" "$temporary_dir/runtime-db-no-tls.log"

export COOLIFY_TEST_MODE=runtime-placeholder
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-placeholder.log" 2>&1; then
  echo "O teste esperava bloquear valores placeholder em runtime." >&2
  exit 1
fi
grep -q "valores de exemplo" "$temporary_dir/runtime-placeholder.log"
if grep -Fq "SYNTHETIC_SECRET_CANARY" "$temporary_dir/runtime-placeholder.log"; then
  echo "Um valor de runtime apareceu na saída do preflight." >&2
  exit 1
fi
export COOLIFY_TEST_MODE=runtime-masked
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/runtime-masked.log" 2>&1; then
  echo "O teste esperava bloquear quando a API oculta os valores de runtime." >&2
  exit 1
fi
grep -q "ocultou ou mascarou" "$temporary_dir/runtime-masked.log"
export COOLIFY_TEST_MODE=success

export COOLIFY_TEST_MODE=bootstrap-missing-unconfirmed
export ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT=false
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/bootstrap-unconfirmed.log" 2>&1; then
  echo "O teste esperava exigir confirmação manual para a API antiga sem /health/deploy-ready." >&2
  exit 1
fi
grep -q "A API bloqueou a release" "$temporary_dir/bootstrap-unconfirmed.log"

export COOLIFY_TEST_MODE=bootstrap-old-api
export ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT=true
rm -f "$COOLIFY_TEST_DEPLOY_READY_COUNT_FILE"
bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/bootstrap-preflight.log"
grep -q "a confirmação manual são o gate de bootstrap" "$temporary_dir/bootstrap-preflight.log"
if grep -Fq "SYNTHETIC_SECRET_CANARY" "$temporary_dir/preflight.log"; then
  echo "Um valor de runtime apareceu na saída do preflight aprovado." >&2
  exit 1
fi

for scenario in preflight-health-failure preflight-health-wrong-body; do
  export COOLIFY_TEST_MODE="$scenario"
  if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/$scenario.log" 2>&1; then
    echo "O teste esperava bloquear a promoção quando a API atual não está Ready ($scenario)." >&2
    exit 1
  fi
  grep -q "A API atual não confirmou /health/ready = Ready" "$temporary_dir/$scenario.log"
  if [[ -f "$COOLIFY_TEST_TRIGGER_COUNT_FILE" ]]; then
    echo "O teste esperava que a readiness inválida bloqueasse antes de qualquer webhook." >&2
    exit 1
  fi
done
for scenario in preflight-active-session preflight-command-in-flight; do
  export COOLIFY_TEST_MODE="$scenario"
  if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/$scenario.log" 2>&1; then
    echo "O teste esperava bloquear a promoção com venda ou comando MDB em andamento ($scenario)." >&2
    exit 1
  fi
  grep -q "A API bloqueou a release:" "$temporary_dir/$scenario.log"
  if [[ -f "$COOLIFY_TEST_TRIGGER_COUNT_FILE" ]]; then
    echo "O teste esperava que o bloqueio operacional acontecesse antes de qualquer webhook." >&2
    exit 1
  fi
done
unset COOLIFY_TEST_MODE

export COOLIFY_TEST_MODE=rolling-port
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/rolling-port.log" 2>&1; then
  echo "O teste esperava bloquear rolling update com porta publicada no host." >&2
  exit 1
fi
grep -q "ports_mappings publica porta no host" "$temporary_dir/rolling-port.log"

export COOLIFY_TEST_MODE=rolling-health
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/rolling-health.log" 2>&1; then
  echo "O teste esperava bloquear rolling update sem health check ativo." >&2
  exit 1
fi
grep -q "Configure health check ativo" "$temporary_dir/rolling-health.log"

for scenario in rolling-compose rolling-git-buildpack rolling-fixed-name rolling-fixed-ip rolling-grace rolling-grace-string rolling-grace-bool rolling-auto-deploy rolling-auto-deploy-missing rolling-wrong-image rolling-wrong-image-tag; do
  export COOLIFY_TEST_MODE="$scenario"
  if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/$scenario.log" 2>&1; then
    echo "O teste esperava bloquear rolling update no cenário $scenario." >&2
    exit 1
  fi
done
grep -q "Deploy automático do Coolify precisa estar explicitamente desativado" "$temporary_dir/rolling-auto-deploy.log"
grep -q "Deploy automático do Coolify precisa estar explicitamente desativado" "$temporary_dir/rolling-auto-deploy-missing.log"
grep -q "A aplicação Coolify deve usar Docker Image" "$temporary_dir/rolling-compose.log"
grep -q "A aplicação Coolify deve usar Docker Image" "$temporary_dir/rolling-git-buildpack.log"
grep -q "não corresponde ao repositório GHCR" "$temporary_dir/rolling-wrong-image.log"
grep -q "não corresponde à tag promovida" "$temporary_dir/rolling-wrong-image-tag.log"
export COOLIFY_TEST_MODE=rolling-wrong-domain
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/rolling-wrong-domain.log" 2>&1; then
  echo "O teste esperava bloquear webhook Coolify associado a outro domínio." >&2
  exit 1
fi
grep -q "não corresponde ao host do health check de produção" "$temporary_dir/rolling-wrong-domain.log"
grep -q "Consistent Container Names desativado" "$temporary_dir/rolling-fixed-name.log"
grep -q "IP fixo" "$temporary_dir/rolling-fixed-ip.log"
grep -q "pelo menos 60 segundos" "$temporary_dir/rolling-grace.log"
grep -q "pelo menos 60 segundos" "$temporary_dir/rolling-grace-string.log"
grep -q "pelo menos 60 segundos" "$temporary_dir/rolling-grace-bool.log"
export COOLIFY_TEST_MODE=success

export COOLIFY_TEST_MODE=rolling-wrong-domain
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/deploy-revalidation.log" 2>&1; then
  echo "O teste esperava revalidar o domínio imediatamente antes do webhook." >&2
  exit 1
fi
grep -q "não corresponde ao host do health check de produção" "$temporary_dir/deploy-revalidation.log"
if [[ -f "$COOLIFY_TEST_TRIGGER_COUNT_FILE" ]]; then
  echo "O webhook foi enviado apesar da configuração Coolify divergente." >&2
  exit 1
fi

export COOLIFY_TEST_MODE=deploy-active-session
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/deploy-active-session.log" 2>&1; then
  echo "O teste esperava revalidar a ausência de venda/comando imediatamente antes do webhook." >&2
  exit 1
fi
grep -q "A API bloqueou a release:" "$temporary_dir/deploy-active-session.log"
if [[ -f "$COOLIFY_TEST_TRIGGER_COUNT_FILE" ]]; then
  echo "O webhook foi enviado apesar de atividade operacional em andamento." >&2
  exit 1
fi

export COOLIFY_TEST_MODE=success
export COOLIFY_DEPLOYMENT_STAGE=post-deploy
: > "$COOLIFY_TEST_HEALTH_COUNT_FILE"

bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/success.log"
grep -q "Logs do Coolify confirmaram.*rolling update" "$temporary_dir/success.log"
grep -q "Coolify marcou.*concluído" "$temporary_dir/success.log"
grep -q "health/ready confirmou status Ready (HTTP 200)" "$temporary_dir/success.log"
if grep -Fq "$COOLIFY_API_TOKEN" "$temporary_dir/success.log"; then
  echo "O token Coolify apareceu na saída do teste." >&2
  exit 1
fi

export COOLIFY_TEST_MODE=bootstrap-old-api
export ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT=true
export COOLIFY_DEPLOYMENT_STAGE=post-deploy
rm -f "$COOLIFY_TEST_DEPLOY_READY_COUNT_FILE"
bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/bootstrap-deploy.log"
grep -q "a confirmação manual são o gate de bootstrap" "$temporary_dir/bootstrap-deploy.log"
grep -q "endpoint de segurança da release está ativo" "$temporary_dir/bootstrap-deploy.log"

export COOLIFY_TEST_MODE=bootstrap-still-missing
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/bootstrap-still-missing.log" 2>&1; then
  echo "O teste esperava falhar quando a versão implantada não instala /health/deploy-ready." >&2
  exit 1
fi
grep -q "não ficou pronta" "$temporary_dir/bootstrap-still-missing.log"
export COOLIFY_TEST_MODE=success
export ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT=false

status_count_before_wrong_target="$(cat "$COOLIFY_TEST_STATUS_COUNT_FILE")"
export COOLIFY_TEST_MODE=wrong-target
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/wrong-target.log" 2>&1; then
  echo "O teste esperava rejeitar deployment_uuid associado a outro recurso." >&2
  exit 1
fi
grep -q "deployment_uuid válido associado à aplicação esperada" "$temporary_dir/wrong-target.log"
if [[ "$(cat "$COOLIFY_TEST_STATUS_COUNT_FILE")" != "$status_count_before_wrong_target" ]]; then
  echo "O script consultou deployment de um recurso inesperado." >&2
  exit 1
fi

export COOLIFY_TEST_MODE=rolling-missing
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/rolling-missing.log" 2>&1; then
  echo "O teste esperava falha quando os logs não comprovam rolling update." >&2
  exit 1
fi
grep -q "sem registrar todos os marcos de rolling update" "$temporary_dir/rolling-missing.log"

export COOLIFY_TEST_MODE=failed
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/failure.log" 2>&1; then
  echo "O teste esperava falha quando o Coolify reporta deployment failed." >&2
  exit 1
fi
grep -q "estado 'failed'" "$temporary_dir/failure.log"

export COOLIFY_TEST_MODE=unknown
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/unknown.log" 2>&1; then
  echo "O teste esperava falha para estado desconhecido do Coolify." >&2
  exit 1
fi
grep -q "Estado do Coolify não reconhecido" "$temporary_dir/unknown.log"

export COOLIFY_DEPLOY_WEBHOOK="http://coolify.example/api/v1/deploy?uuid=app-test"
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" preflight > "$temporary_dir/invalid-url.log" 2>&1; then
  echo "O teste esperava rejeitar webhook sem HTTPS." >&2
  exit 1
fi
grep -q "precisa ser HTTPS" "$temporary_dir/invalid-url.log"
export COOLIFY_DEPLOY_WEBHOOK="https://coolify.example/api/v1/deploy?uuid=app-test&force=false"
export COOLIFY_TEST_MODE=health-failure
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/health-failure.log" 2>&1; then
  echo "O teste esperava falha quando /health/ready não recupera." >&2
  exit 1
fi
grep -q "não ficou pronta" "$temporary_dir/health-failure.log"

export COOLIFY_TEST_MODE=health-wrong-body
if bash "$repo_root/tools/Deployment/Coolify-Deployment.sh" deploy > "$temporary_dir/health-body-failure.log" 2>&1; then
  echo "O teste esperava falha quando /health/ready responde HTTP 200 com estado NotReady." >&2
  exit 1
fi
grep -q "não ficou pronta" "$temporary_dir/health-body-failure.log"

echo "Coolify-Deployment.sh: auto deploy bloqueado, imagem GHCR/tag conferidas, domínio do webhook, elegibilidade/logs de rolling update, portas host, health check, deploy e readiness verificados."
