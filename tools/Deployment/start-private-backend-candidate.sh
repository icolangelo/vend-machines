#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Uso:
  start-private-backend-candidate.sh \
    --artifact-dir DIR \
    --release-package-dir DIR \
    --expected-source-sha SHA40 \
    --docker-network NETWORK \
    --runtime-env-file FILE \
    [--active-overlap] \
    [--legacy-api-origin URL] \
    [--host-port PORT]

Por padrão inicia em modo passivo (somente health/readiness). Use --active-overlap
apenas após os gates operacionais; nesse modo --legacy-api-origin é obrigatório.
Inicia a imagem verificada em container separado, publicada somente em loopback.
Não aplica migrations, não adiciona rota pública e não remove containers existentes.
USAGE
}

fail() {
  printf 'Falha: %s\n' "$1" >&2
  exit 1
}

artifact_dir=''
release_package_dir=''
expected_source_sha=''
docker_network=''
runtime_env_file=''
legacy_api_origin=''
host_port='18080'
active_overlap=0

while (($# > 0)); do
  case "$1" in
    --artifact-dir) (($# >= 2)) || { usage >&2; exit 2; }; artifact_dir=$2; shift 2 ;;
    --release-package-dir) (($# >= 2)) || { usage >&2; exit 2; }; release_package_dir=$2; shift 2 ;;
    --expected-source-sha) (($# >= 2)) || { usage >&2; exit 2; }; expected_source_sha=$2; shift 2 ;;
    --docker-network) (($# >= 2)) || { usage >&2; exit 2; }; docker_network=$2; shift 2 ;;
    --runtime-env-file) (($# >= 2)) || { usage >&2; exit 2; }; runtime_env_file=$2; shift 2 ;;
    --legacy-api-origin) (($# >= 2)) || { usage >&2; exit 2; }; legacy_api_origin=$2; shift 2 ;;
    --active-overlap) active_overlap=1; shift ;;
    --host-port) (($# >= 2)) || { usage >&2; exit 2; }; host_port=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "argumento desconhecido: $1" ;;
  esac
done

[[ "$expected_source_sha" =~ ^[A-Fa-f0-9]{40}$ ]] || fail 'informe o SHA Git completo esperado (40 caracteres hexadecimais).'
[[ -n "$artifact_dir" && -n "$release_package_dir" && -n "$docker_network" && -n "$runtime_env_file" ]] || { usage >&2; exit 2; }
if ((active_overlap)) && [[ -z "$legacy_api_origin" ]]; then
  fail '--legacy-api-origin é obrigatório com --active-overlap.'
fi
[[ "$host_port" =~ ^[0-9]{1,5}$ ]] && ((10#$host_port >= 1 && 10#$host_port <= 65535)) || fail 'a porta local precisa estar entre 1 e 65535.'

artifact_dir=$(cd -- "$artifact_dir" 2>/dev/null && pwd -P) || fail 'diretório do artefato não encontrado.'
release_package_dir=$(cd -- "$release_package_dir" 2>/dev/null && pwd -P) || fail 'diretório do pacote operacional não encontrado.'
runtime_env_file=$(cd -- "$(dirname -- "$runtime_env_file")" 2>/dev/null && printf '%s/%s\n' "$(pwd -P)" "$(basename -- "$runtime_env_file")") || fail 'arquivo de runtime não encontrado.'
[[ -f "$runtime_env_file" && ! -L "$runtime_env_file" && -r "$runtime_env_file" ]] || fail 'arquivo de runtime precisa existir, ser legível e não ser link simbólico.'
[[ -f "$artifact_dir/backend-image.tar.gz" ]] || fail 'imagem compactada ausente.'
if [[ "$runtime_env_file" == "$artifact_dir/"* ]]; then
  fail 'o arquivo de runtime precisa ficar fora do diretório do artefato.'
fi
if [[ "$runtime_env_file" == "$release_package_dir/"* ]]; then
  fail 'o arquivo de runtime precisa ficar fora do pacote operacional.'
fi
runtime_file_mode=$(stat -c '%a' -- "$runtime_env_file" 2>/dev/null) || fail 'não foi possível verificar as permissões do arquivo de runtime.'
[[ "$runtime_file_mode" =~ ^[0-7]{3,4}$ ]] || fail 'permissões do arquivo de runtime inválidas.'
(( (8#$runtime_file_mode & 077) == 0 )) || fail 'o arquivo de runtime precisa negar leitura e escrita para grupo e outros (por exemplo, chmod 600).'

if [[ -n "$legacy_api_origin" ]]; then
python3 - "$legacy_api_origin" <<'PY' || fail 'a origem legada deve ser uma URL HTTP privada direta, sem credenciais, caminho, query ou fragmento.'
import ipaddress
import sys
from urllib.parse import urlsplit

value = sys.argv[1]
try:
    parsed = urlsplit(value)
    hostname = parsed.hostname
    port = parsed.port
except (ValueError, TypeError):
    raise SystemExit(1)
try:
    loopback = ipaddress.ip_address(hostname).is_loopback if hostname else False
    global_address = ipaddress.ip_address(hostname).is_global if hostname else False
except ValueError:
    loopback = False
    global_address = False

if (
    parsed.scheme != "http"
    or not hostname
    or parsed.username is not None
    or parsed.password is not None
    or parsed.path not in ("", "/")
    or parsed.query
    or parsed.fragment
    or loopback
    or global_address
    or (port is not None and not 1 <= port <= 65535)
    or hostname.lower().rstrip(".") == "api.vendmachine.com.br"
):
    raise SystemExit(1)
PY
fi

[[ "$(grep -c '^ASPNETCORE_ENVIRONMENT=Production$' "$runtime_env_file" || true)" == 1 ]] || fail 'ASPNETCORE_ENVIRONMENT=Production precisa estar definido exatamente uma vez no arquivo privado de runtime.'
[[ "$(grep -c '^DOTNET_ENVIRONMENT=Production$' "$runtime_env_file" || true)" == 1 ]] || fail 'DOTNET_ENVIRONMENT=Production precisa estar definido exatamente uma vez no arquivo privado de runtime.'
if grep -Eiq '^Deployment__PassiveMode=' "$runtime_env_file"; then
  fail 'não defina Deployment__PassiveMode no arquivo de runtime; o launcher controla esse modo explicitamente.'
fi

python3 "$artifact_dir/verify-candidate-artifact-pair.py" \
  --image-artifact-dir "$artifact_dir" \
  --release-package-dir "$release_package_dir" \
  --expected-source-sha "$expected_source_sha" || fail 'imagem e pacote operacional não correspondem ao mesmo SHA ou algum checksum diverge.'

required_files=(
  backend-image.tar.gz
  image-id.txt
  image-ref.txt
  platform.txt
  source-sha.txt
  README.txt
  Verify-BackendImageArtifact.ps1
  start-private-backend-candidate.sh
  verify-candidate-artifact-pair.py
)
expected_entries=($(printf '%s\n' "${required_files[@]}" SHA256SUMS | sort))
actual_entries=()
while IFS= read -r -d '' entry; do
  name=$(basename -- "$entry")
  [[ -f "$entry" && ! -L "$entry" ]] || fail 'o diretório contém link simbólico, pasta ou arquivo não regular.'
  actual_entries+=("$name")
done < <(find "$artifact_dir" -mindepth 1 -maxdepth 1 -print0)
mapfile -t actual_entries < <(printf '%s\n' "${actual_entries[@]}" | sort)
[[ "${actual_entries[*]}" == "${expected_entries[*]}" ]] || fail 'o diretório extraído contém arquivos extras ou incompletos.'

declare -A manifest_files=()
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^([A-Fa-f0-9]{64})\ \ ([^[:space:]/\\]+)$ ]] || fail 'SHA256SUMS contém uma entrada inválida.'
  file_name=${BASH_REMATCH[2]}
  [[ "$file_name" != 'SHA256SUMS' ]] || fail 'SHA256SUMS não pode listar a si próprio.'
  [[ -z "${manifest_files[$file_name]+present}" ]] || fail 'SHA256SUMS contém arquivo duplicado.'
  manifest_files[$file_name]=1
done < "$artifact_dir/SHA256SUMS"
[[ "${#manifest_files[@]}" -eq "${#required_files[@]}" ]] || fail 'SHA256SUMS precisa cobrir exatamente os arquivos do artefato.'
for file_name in "${required_files[@]}"; do
  [[ -n "${manifest_files[$file_name]+present}" ]] || fail "SHA256SUMS não cobre $file_name."
done
(cd -- "$artifact_dir" && sha256sum --check --strict --quiet SHA256SUMS) || fail 'checksum inválido; imagem não carregada.'

source_sha=$(tr -d '\r\n' < "$artifact_dir/source-sha.txt")
[[ "${source_sha,,}" == "${expected_source_sha,,}" ]] || fail 'SHA de origem diverge do SHA esperado.'
image_ref=$(tr -d '\r\n' < "$artifact_dir/image-ref.txt")
[[ "$image_ref" == "vend-machines-backend:$expected_source_sha" ]] || fail 'referência de imagem não corresponde ao SHA esperado.'
image_id=$(tr -d '\r\n' < "$artifact_dir/image-id.txt")
[[ "$image_id" =~ ^sha256:[A-Fa-f0-9]{64}$ ]] || fail 'ID da imagem está ausente ou malformado.'

artifact_platform=$(tr -d '\r\n' < "$artifact_dir/platform.txt" | tr '[:upper:]' '[:lower:]')
daemon_platform=$(docker info --format '{{.OSType}}/{{.Architecture}}' 2>/dev/null | tr '[:upper:]' '[:lower:]') || fail 'daemon Docker indisponível.'
daemon_platform=${daemon_platform/linux\/x86_64/linux\/amd64}
daemon_platform=${daemon_platform/linux\/aarch64/linux\/arm64}
[[ "$artifact_platform" =~ ^linux/(amd64|arm64)$ && "$artifact_platform" == "$daemon_platform" ]] || fail 'plataforma do artefato não corresponde ao daemon Docker.'

docker network inspect "$docker_network" >/dev/null 2>&1 || fail 'a rede Docker informada não existe ou não está acessível.'
container_name="vend-machines-backend-candidate-${expected_source_sha,,}"
if docker container inspect "$container_name" >/dev/null 2>&1; then
  fail "o container $container_name já existe; não será substituído nem removido."
fi

docker load --input "$artifact_dir/backend-image.tar.gz" >/dev/null || fail 'não foi possível carregar a imagem verificada.'
loaded_image_id=$(docker image inspect --format '{{.Id}}' "$image_ref" 2>/dev/null) || fail 'imagem com a referência esperada não foi carregada.'
[[ "${loaded_image_id,,}" == "${image_id,,}" ]] || fail 'o ID da imagem carregada diverge do manifesto.'

passive_mode=true
if ((active_overlap)); then
  passive_mode=false
fi
docker_env_args=(--env "Deployment__PassiveMode=$passive_mode")
if [[ -n "$legacy_api_origin" ]]; then
  docker_env_args+=(--env "Telemetry__LegacyApiBaseUrl=$legacy_api_origin")
fi

docker run --detach \
  --name "$container_name" \
  --network "$docker_network" \
  --publish "127.0.0.1:${host_port}:8080" \
  --env-file "$runtime_env_file" \
  "${docker_env_args[@]}" \
  "$image_ref" >/dev/null || fail 'Docker não conseguiu iniciar a candidata privada.'

health_status='starting'
for _ in $(seq 1 90); do
  health_status=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_name" 2>/dev/null || printf 'unavailable')
  case "$health_status" in
    healthy) break ;;
    unhealthy|missing|unavailable) break ;;
  esac
  sleep 2
done

if [[ "$health_status" != 'healthy' ]]; then
  if docker stop --time 45 "$container_name" >/dev/null 2>&1; then
    fail "a candidata não ficou saudável (estado: $health_status); o container candidato foi parado sem alterar a API anterior."
  fi
  fail "a candidata não ficou saudável (estado: $health_status) e não foi possível confirmar a parada do container; verifique-o imediatamente. A API anterior não foi alterada."
fi

printf 'Candidata iniciada: container=%s sha=%s endpoint=http://127.0.0.1:%s\n' \
  "$container_name" "$expected_source_sha" "$host_port"
if ((active_overlap)); then
  printf 'Modo ativo de convivência explicitamente solicitado.\n'
  printf 'Execute Validate-TelemetryOverlapReadiness.py antes de encaminhar tráfego.\n'
else
  printf 'Modo passivo: rotas comerciais e WebSockets permanecem bloqueados.\n'
  printf 'Para validar convivência ativa, repita com --active-overlap somente após os gates operacionais.\n'
fi
printf 'Health check aprovado; publicação limitada a loopback, sem configurar rota pública.\n'
