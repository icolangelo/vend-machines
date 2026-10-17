#!/usr/bin/env bash
set -euo pipefail

action="${1:-}"
if [[ "$action" != "preflight" && "$action" != "deploy" ]]; then
  echo "Uso: Coolify-Deployment.sh preflight|deploy" >&2
  exit 2
fi

: "${COOLIFY_DEPLOY_WEBHOOK:?Configure COOLIFY_BACKEND_DEPLOY_WEBHOOK no ambiente production.}"
: "${COOLIFY_API_TOKEN:?Configure COOLIFY_API_TOKEN no ambiente production.}"
: "${PRODUCTION_API_HEALTH_URL:?Configure PRODUCTION_API_HEALTH_URL para a API publicada.}"
: "${PRODUCTION_API_DEPLOY_READY_URL:?Configure PRODUCTION_API_DEPLOY_READY_URL para a API publicada.}"
: "${REGISTRY:?Configure REGISTRY para validar a origem da imagem Coolify.}"
: "${IMAGE_NAME:?Configure IMAGE_NAME para validar a origem da imagem Coolify.}"
allow_missing_deploy_readiness_endpoint="${ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT:-false}"
if [[ "$allow_missing_deploy_readiness_endpoint" != "true" && "$allow_missing_deploy_readiness_endpoint" != "false" ]]; then
  echo "::error::ALLOW_MISSING_DEPLOY_READINESS_ENDPOINT deve ser true ou false." >&2
  exit 2
fi

coolify_endpoint_data="$(python3 - <<'PY'
import os
import re
from urllib.parse import parse_qs, urlsplit

webhook = urlsplit(os.environ["COOLIFY_DEPLOY_WEBHOOK"])
application_ids = parse_qs(webhook.query).get("uuid", [])
if (webhook.scheme != "https" or not webhook.netloc or webhook.username or
        webhook.password or webhook.fragment or
        not webhook.path.endswith("/api/v1/deploy") or len(application_ids) != 1 or
        not re.fullmatch(r"[A-Za-z0-9_-]+", application_ids[0])):
    raise SystemExit("COOLIFY_BACKEND_DEPLOY_WEBHOOK precisa ser HTTPS e apontar para /api/v1/deploy?uuid=...")

health = urlsplit(os.environ["PRODUCTION_API_HEALTH_URL"])
if (health.scheme != "https" or not health.netloc or health.username or
        health.password or health.fragment or health.path != "/health/ready" or not health.hostname):
    raise SystemExit("PRODUCTION_API_HEALTH_URL precisa ser HTTPS e terminar em /health/ready")

deploy_ready = urlsplit(os.environ["PRODUCTION_API_DEPLOY_READY_URL"])
if (deploy_ready.scheme != "https" or not deploy_ready.netloc or deploy_ready.username or
        deploy_ready.password or deploy_ready.fragment or deploy_ready.path != "/health/deploy-ready" or
        deploy_ready.hostname != health.hostname):
    raise SystemExit("PRODUCTION_API_DEPLOY_READY_URL precisa ser HTTPS no mesmo host e terminar em /health/deploy-ready")

api_path = webhook.path[:-len("/deploy")]
print(f"{webhook.scheme}://{webhook.netloc}{api_path}\t{application_ids[0]}\t{health.hostname}\t{deploy_ready.hostname}")
PY
)"
IFS=$'\t' read -r coolify_api_base_url coolify_application_uuid production_api_hostname deploy_ready_hostname <<< "$coolify_endpoint_data"

validate_production_deploy_readiness() {
  local response_file="$1"
  local http_status
  local block_reason
  if ! http_status="$(curl --max-time 15 -sS -o "$response_file" -w '%{http_code}' \
    --header "Accept: application/json" "$PRODUCTION_API_DEPLOY_READY_URL")"; then
    echo "::error::Não foi possível consultar o bloqueio de sessões/comandos antes da release. Nenhuma chamada ao Coolify foi enviada."
    return 1
  fi

  if [[ "$http_status" == "404" && "$allow_missing_deploy_readiness_endpoint" == "true" ]]; then
    echo "::warning::A API atual ainda não possui /health/deploy-ready. O SQL somente leitura e a confirmação manual são o gate de bootstrap; a versão nova precisa instalar a rota após o rolling update."
    return 0
  fi

  block_reason="$(python3 - "$response_file" <<'PY' || true
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        payload = json.load(response)
except (OSError, json.JSONDecodeError):
    print("a resposta do endpoint operacional é inválida")
    raise SystemExit(1)

if isinstance(payload, dict) and payload.get("status") == "Ready":
    raise SystemExit(0)

reason = payload.get("reason") if isinstance(payload, dict) else None
descriptions = {
    "active_vend_sessions": "há uma venda física em andamento",
    "telemetry_commands_in_flight": "há comando MDB aguardando conclusão",
    "deployment_preflight_failed": "a consulta operacional falhou fechada",
}
print(descriptions.get(reason, "a API não autorizou o preflight operacional"))
raise SystemExit(1)
PY
  )"

  if [[ ! "$http_status" =~ ^2[0-9][0-9]$ || -n "$block_reason" ]]; then
    [[ -n "$block_reason" ]] || block_reason="a consulta operacional retornou HTTP $http_status"
    echo "::error::A API bloqueou a release: $block_reason. Aguarde e repita a consulta; não interrompa vendas nem comandos."
    return 1
  fi
}

verify_deploy_readiness_endpoint_installed() {
  local response_file="$1"
  local http_status
  local endpoint_state
  if ! http_status="$(curl --max-time 15 -sS -o "$response_file" -w '%{http_code}' \
    --header "Accept: application/json" "$PRODUCTION_API_DEPLOY_READY_URL")"; then
    return 1
  fi

  endpoint_state="$(python3 - "$http_status" "$response_file" <<'PY' || true
import json
import sys

try:
    status_code = int(sys.argv[1])
    with open(sys.argv[2], encoding="utf-8") as response:
        payload = json.load(response)
except (OSError, ValueError, json.JSONDecodeError):
    raise SystemExit(1)

if status_code == 200 and isinstance(payload, dict) and payload.get("status") == "Ready":
    print("ready")
    raise SystemExit(0)

if status_code == 503 and isinstance(payload, dict) and payload.get("status") == "NotReady":
    reason = payload.get("reason")
    if reason in {"active_vend_sessions", "telemetry_commands_in_flight"}:
        print("installed_with_live_activity")
        raise SystemExit(0)
PY
  )"

  case "$endpoint_state" in
    ready)
      echo "O endpoint de segurança da release está ativo e sem vendas/comandos em curso."
      return 0
      ;;
    installed_with_live_activity)
      echo "::warning::O endpoint de segurança da release está instalado; há nova atividade operacional após o rolling update."
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

validate_coolify_application() {
  local response_file="$1"
  local environment_file="$2"
  local app_http_status
  if ! app_http_status="$(curl --max-time 30 -sS -o "$response_file" -w '%{http_code}' \
    --header "Authorization: Bearer $COOLIFY_API_TOKEN" \
    --header "Accept: application/json" \
    "$coolify_api_base_url/applications/$coolify_application_uuid")"; then
    echo "::error::Não foi possível ler a configuração da aplicação Coolify. Nenhuma requisição de deploy foi enviada."
    return 1
  fi
  if [[ ! "$app_http_status" =~ ^2[0-9][0-9]$ ]]; then
    echo "::error::A API do Coolify não permitiu consultar a aplicação indicada pelo webhook (HTTP $app_http_status)."
    return 1
  fi
  python3 tools/Deployment/Validate-Coolify-RollingUpdate.py \
    "$response_file" "$coolify_application_uuid" "$production_api_hostname" \
    "${REGISTRY,,}/${IMAGE_NAME,,}" "latest"

  local environment_http_status
  if ! environment_http_status="$(curl --max-time 30 -sS -o "$environment_file" -w '%{http_code}' \
    --header "Authorization: Bearer $COOLIFY_API_TOKEN" \
    --header "Accept: application/json" \
    "$coolify_api_base_url/applications/$coolify_application_uuid/envs")"; then
    echo "::error::Não foi possível consultar as variáveis de runtime da aplicação Coolify. Nenhum webhook foi enviado."
    return 1
  fi
  if [[ ! "$environment_http_status" =~ ^2[0-9][0-9]$ ]]; then
    echo "::error::A API do Coolify não permitiu consultar as variáveis da aplicação (HTTP $environment_http_status). Nenhum webhook foi enviado."
    return 1
  fi
  python3 tools/Deployment/Validate-Coolify-RuntimeEnvironment.py "$environment_file"
}

if [[ "$action" == "preflight" ]]; then
  response_dir="$(mktemp -d)"
  trap 'rm -f "$response_dir/application.json" "$response_dir/environment.json"; rmdir "$response_dir"' EXIT
  response_file="$response_dir/application.json"
  validate_coolify_application "$response_file" "$response_dir/environment.json"

  echo "Verificando readiness atual da API antes de promover a imagem."
  if ! current_health_status="$(curl --max-time 15 -sS -o "$response_file" -w '%{http_code}' \
    --header "Accept: application/json" "$PRODUCTION_API_HEALTH_URL")" ||
      [[ ! "$current_health_status" =~ ^2[0-9][0-9]$ ]] ||
      ! python3 - "$response_file" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        payload = json.load(response)
except (OSError, json.JSONDecodeError):
    raise SystemExit(1)

if not isinstance(payload, dict) or payload.get("status") != "Ready":
    raise SystemExit(1)
PY
  then
    echo "::error::A API atual não confirmou /health/ready = Ready. Nenhuma tag foi promovida e nenhum webhook foi enviado."
    exit 1
  fi

  if ! validate_production_deploy_readiness "$response_file"; then
    exit 1
  fi

  if ! http_status="$(curl --max-time 30 -sS -o "$response_file" -w '%{http_code}' \
    --header "Authorization: Bearer $COOLIFY_API_TOKEN" \
    --header "Accept: application/json" \
    "$coolify_api_base_url/deployments")"; then
    echo "::error::Não foi possível consultar a API do Coolify com o token configurado. Nenhuma tag de produção foi promovida."
    exit 1
  fi

  if [[ ! "$http_status" =~ ^2[0-9][0-9]$ ]] || ! python3 - "$response_file" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        payload = json.load(response)
except (OSError, json.JSONDecodeError):
    raise SystemExit(1)
if not isinstance(payload, list):
    raise SystemExit(1)
PY
  then
    echo "::error::A API do Coolify não aceitou a consulta de deployments. Configure um token com permissões read e deploy. Nenhuma tag de produção foi promovida."
    exit 1
  fi

  echo "Readiness atual, ausência de venda/comando em curso, configuração de continuidade e acesso de leitura do Coolify validados antes da promoção da imagem."
  exit 0
fi

response_dir="$(mktemp -d)"
trap 'rm -f "$response_dir/application.json" "$response_dir/environment.json" "$response_dir/trigger.json" "$response_dir/status.json" "$response_dir/health.json" "$response_dir/deploy-ready.json"; rmdir "$response_dir"' EXIT
echo "Revalidando domínio, elegibilidade para rolling update e health check antes do webhook."
validate_coolify_application "$response_dir/application.json" "$response_dir/environment.json"
validate_production_deploy_readiness "$response_dir/deploy-ready.json"
trigger_response="$response_dir/trigger.json"
if ! trigger_status="$(curl --max-time 30 -sS -o "$trigger_response" -w '%{http_code}' \
  --request POST "$COOLIFY_DEPLOY_WEBHOOK" \
  --header "Authorization: Bearer $COOLIFY_API_TOKEN" \
  --header "Accept: application/json")"; then
  echo "::error::Não foi possível enviar o pedido de deploy ao Coolify. Verifique o estado no painel antes de repetir a release."
  exit 1
fi

deployment_uuid=""
if [[ "$trigger_status" =~ ^2[0-9][0-9]$ ]]; then
  deployment_uuid="$(python3 - "$trigger_response" "$coolify_application_uuid" <<'PY'
import json
import re
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        payload = json.load(response)
    deployments = payload.get("deployments")
    first = deployments[0] if isinstance(deployments, list) and deployments else None
    value = first.get("deployment_uuid") if isinstance(first, dict) else None
    resource_uuid = first.get("resource_uuid") if isinstance(first, dict) else None
except (OSError, json.JSONDecodeError, AttributeError, TypeError):
    value = None
    resource_uuid = None

if (resource_uuid == sys.argv[2] and isinstance(value, str) and
        re.fullmatch(r"[A-Za-z0-9_-]+", value)):
    print(value)
PY
  )"
fi

if [[ -z "$deployment_uuid" ]]; then
  echo "::error::O Coolify não retornou um deployment_uuid válido associado à aplicação esperada. A requisição pode ter sido aceita; confira o painel antes de repetir."
  exit 1
fi

deploy_timeout="${COOLIFY_DEPLOY_TIMEOUT_SECONDS:-900}"
poll_interval="${COOLIFY_DEPLOY_POLL_INTERVAL_SECONDS:-15}"
health_timeout="${POST_DEPLOY_HEALTH_TIMEOUT_SECONDS:-300}"
health_interval="${POST_DEPLOY_HEALTH_INTERVAL_SECONDS:-10}"
if ! [[ "$deploy_timeout" =~ ^[1-9][0-9]*$ && "$poll_interval" =~ ^[0-9]+$ &&
        "$health_timeout" =~ ^[1-9][0-9]*$ && "$health_interval" =~ ^[0-9]+$ ]]; then
  echo "::error::Os limites de espera do deploy contêm valores inválidos."
  exit 1
fi

echo "Deploy enfileirado no Coolify. Aguardando conclusão (UUID $deployment_uuid)."
deploy_deadline=$(( $(date +%s) + deploy_timeout ))
deployment_finished=false
while (( $(date +%s) < deploy_deadline )); do
  status_response="$response_dir/status.json"
  if status_http="$(curl --max-time 20 -sS -o "$status_response" -w '%{http_code}' \
    --header "Authorization: Bearer $COOLIFY_API_TOKEN" \
    --header "Accept: application/json" \
    "$coolify_api_base_url/deployments/$deployment_uuid")"; then
    if [[ "$status_http" == "200" ]]; then
      deployment_status="$(python3 - "$status_response" <<'PY'
import json
import re
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        value = json.load(response).get("status")
except (OSError, json.JSONDecodeError, AttributeError, TypeError):
    value = None

if isinstance(value, str) and re.fullmatch(r"[A-Za-z_-]+", value):
    print(value.lower())
PY
      )"
      case "$deployment_status" in
        finished)
          deployment_finished=true
          break
          ;;
        queued|in_progress)
          ;;
        failed|cancelled|cancelled-by-user)
          echo "::error::O Coolify encerrou o deploy com estado '$deployment_status'. A imagem permanece publicada; siga o procedimento manual de rollback."
          exit 1
          ;;
        "")
          echo "::error::A API do Coolify retornou um estado de deployment inválido."
          exit 1
          ;;
        *)
          echo "::error::Estado do Coolify não reconhecido: '$deployment_status'. Verifique o deployment manualmente."
          exit 1
          ;;
      esac
    elif [[ "$status_http" == "401" || "$status_http" == "403" ]]; then
      echo "::error::O token do Coolify não pode consultar o deployment. Confirme as permissões read e deploy."
      exit 1
    elif [[ "$status_http" != "404" && ! "$status_http" =~ ^5[0-9][0-9]$ ]]; then
      echo "::error::A consulta do deployment no Coolify retornou HTTP $status_http. Verifique o painel."
      exit 1
    fi
  fi
  sleep "$poll_interval"
done

if [[ "$deployment_finished" != true ]]; then
  echo "::error::O deploy do Coolify não terminou em ${deploy_timeout}s. Verifique o painel antes de repetir ou fazer rollback."
  exit 1
fi

if ! python3 - "$status_response" <<'PY'
import json
import sys

required_markers = (
    "Rolling update started",
    "New container is healthy",
    "Rolling update completed",
)
try:
    with open(sys.argv[1], encoding="utf-8") as response:
        deployment = json.load(response)
    logs = deployment.get("logs")
except (OSError, json.JSONDecodeError, AttributeError, TypeError):
    raise SystemExit(1)

if not isinstance(logs, str) or any(marker not in logs for marker in required_markers):
    raise SystemExit(1)
PY
then
  echo "::error::O Coolify concluiu o deployment sem registrar todos os marcos de rolling update. Confira os logs e trate a continuidade como não comprovada."
  exit 1
fi
echo "Logs do Coolify confirmaram início, health check saudável e conclusão do rolling update."

echo "O Coolify marcou o deployment como concluído. Validando readiness pública da API."
health_deadline=$(( $(date +%s) + health_timeout ))
health_response="$response_dir/health.json"
while (( $(date +%s) < health_deadline )); do
  if health_http="$(curl --max-time 10 -sS -o "$health_response" -w '%{http_code}' \
    --header "Accept: application/json" "$PRODUCTION_API_HEALTH_URL")" &&
      [[ "$health_http" =~ ^2[0-9][0-9]$ ]]; then
    if python3 - "$health_response" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as response:
        payload = json.load(response)
except (OSError, json.JSONDecodeError):
    raise SystemExit(1)

if not isinstance(payload, dict) or payload.get("status") != "Ready":
    raise SystemExit(1)
PY
    then
      if verify_deploy_readiness_endpoint_installed "$response_dir/deploy-ready.json"; then
        echo "Deploy concluído e /health/ready confirmou status Ready (HTTP $health_http)."
        exit 0
      fi
    fi
  fi
  sleep "$health_interval"
done

echo "::error::O deployment terminou no Coolify, mas a API não ficou pronta em ${health_timeout}s. Verifique logs/roteamento e siga o rollback manual; nenhuma reversão automática foi iniciada."
exit 1
