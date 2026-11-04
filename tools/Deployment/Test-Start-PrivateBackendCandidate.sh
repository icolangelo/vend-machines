#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
launcher="$repo_root/tools/Deployment/start-private-backend-candidate.sh"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT

artifact_directory="$temporary_directory/artifact"
release_directory="$temporary_directory/release"
bin_directory="$temporary_directory/bin"
mkdir -p "$artifact_directory" "$release_directory" "$bin_directory"
docker_root_directory="$temporary_directory/docker-root"
mkdir -p "$docker_root_directory"
source_sha='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
image_id="sha256:$(printf 'b%.0s' {1..64})"
write_image_archive() {
  python3 - "$artifact_directory/backend-image.tar.gz" <<'PY'
import io
import sys
import tarfile

with tarfile.open(sys.argv[1], "w:gz") as archive:
    payload = b"synthetic image layer"
    member = tarfile.TarInfo("fixture-layer.tar")
    member.size = len(payload)
    archive.addfile(member, io.BytesIO(payload))
PY
}
write_image_archive
printf '%s\n' "$image_id" > "$artifact_directory/image-id.txt"
printf 'vend-machines-backend:%s\n' "$source_sha" > "$artifact_directory/image-ref.txt"
printf 'linux/amd64\n' > "$artifact_directory/platform.txt"
printf '%s\n' "$source_sha" > "$artifact_directory/source-sha.txt"
printf 'fixture\n' > "$artifact_directory/README.txt"
cp "$repo_root/tools/Deployment/Verify-BackendImageArtifact.ps1" "$artifact_directory/Verify-BackendImageArtifact.ps1"
cp "$launcher" "$artifact_directory/start-private-backend-candidate.sh"
cp "$repo_root/tools/Deployment/verify-candidate-artifact-pair.py" "$artifact_directory/verify-candidate-artifact-pair.py"
chmod 755 "$artifact_directory/start-private-backend-candidate.sh"
(cd "$artifact_directory" && sha256sum backend-image.tar.gz image-id.txt image-ref.txt platform.txt source-sha.txt README.txt Verify-BackendImageArtifact.ps1 start-private-backend-candidate.sh verify-candidate-artifact-pair.py > SHA256SUMS)
(cd "$release_directory" && printf '%s\n' "$source_sha" > source-sha.txt && printf 'SELECT 1;\n' > postgresql-migration-test.sql && sha256sum source-sha.txt postgresql-migration-test.sql > SHA256SUMS)
printf 'ASPNETCORE_ENVIRONMENT=Production\nDOTNET_ENVIRONMENT=Production\n' > "$temporary_directory/runtime.env"
chmod 600 "$temporary_directory/runtime.env"

cat > "$bin_directory/docker" <<'DOCKER'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$DOCKER_TEST_LOG"
case "$1" in
  info)
    if [[ "$*" == *DockerRootDir* ]]; then
      printf '%s\n' "$DOCKER_TEST_ROOT_DIR"
    else
      printf 'linux/x86_64\n'
    fi
    ;;
  network) exit 0 ;;
  container) exit 1 ;;
  load) exit 0 ;;
  image) printf '%s\n' "$DOCKER_TEST_IMAGE_ID" ;;
  run) printf 'container-id\n' ;;
  inspect) printf '%s\n' "${DOCKER_TEST_HEALTH_STATUS:-healthy}" ;;
  exec)
    if [[ "$#" == 5 ]]; then
      [[ "${DOCKER_TEST_PASSIVE_MODE:-true}" == true ]]
      exit
    fi
    route="${7:-}"
    expected_status="${8:-}"
    case "$route" in
      /health/ready) actual_status="${DOCKER_TEST_PASSIVE_READY_STATUS:-200}" ;;
      /health/deploy-ready) actual_status="${DOCKER_TEST_PASSIVE_DEPLOY_READY_STATUS:-503}" ;;
      /api/machines|/telemetria) actual_status="${DOCKER_TEST_PASSIVE_APPLICATION_STATUS:-503}" ;;
      *) exit 8 ;;
    esac
    [[ "$actual_status" == "$expected_status" ]]
    ;;
  stop) [[ "${DOCKER_TEST_STOP_FAIL:-0}" != 1 ]] ;;
  kill) [[ "${DOCKER_TEST_KILL_FAIL:-0}" != 1 ]] ;;
  *) exit 9 ;;
esac
DOCKER
chmod 755 "$bin_directory/docker"

cat > "$bin_directory/nproc" <<'NPROC'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "${DOCKER_TEST_CPU_COUNT:-8}"
NPROC
cat > "$bin_directory/awk" <<'AWK'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$*" == *MemAvailable:* ]]; then
  printf '%s\n' "$(( ${DOCKER_TEST_MEM_AVAILABLE_KB:-8388608} / 1024 ))"
else
  exec /usr/bin/awk "$@"
fi
AWK
cat > "$bin_directory/df" <<'DF'
#!/usr/bin/env bash
set -euo pipefail
printf 'Filesystem 1M-blocks Used Available Use%% Mounted on\n'
printf 'synthetic 200000 1 %s 1%% /docker\n' "${DOCKER_TEST_DISK_AVAILABLE_MIB:-200000}"
DF
chmod 755 "$bin_directory/nproc" "$bin_directory/awk" "$bin_directory/df"

export PATH="$bin_directory:$PATH"
export DOCKER_TEST_IMAGE_ID="$image_id"
export DOCKER_TEST_LOG="$temporary_directory/docker.log"
export DOCKER_TEST_ROOT_DIR="$docker_root_directory"

resource_arguments=(
  --candidate-cpus 0.25
  --candidate-memory-mib 256
  --reserve-host-cpus 0.25
  --reserve-host-memory-mib 512
  --reserve-docker-disk-mib 512
)
run_candidate_with_resources() {
  bash "$launcher" \
    --artifact-dir "$artifact_directory" \
    --release-package-dir "$release_directory" \
    --expected-source-sha "$source_sha" \
    --docker-network private-network \
    --runtime-env-file "$temporary_directory/runtime.env" \
    "${resource_arguments[@]}" \
    "$@"
}

bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/success.log"
grep -q 'Health check e readiness passivos aprovados' "$temporary_directory/success.log"
grep -q -- '--publish 127.0.0.1:18080:8080' "$DOCKER_TEST_LOG"
grep -q -- '--env Deployment__PassiveMode=true' "$DOCKER_TEST_LOG"
grep -q -- '--cpus 0.25 --memory 256m' "$DOCKER_TEST_LOG"
grep -q -- '--memory-swap 256m' "$DOCKER_TEST_LOG"
! grep -q -- 'Telemetry__LegacyApiBaseUrl=' "$DOCKER_TEST_LOG"
! grep -q -- '--restart' "$DOCKER_TEST_LOG"
grep -q '/health/ready 200' "$DOCKER_TEST_LOG"

for capacity_case in cpu memory disk; do
  rm -f "$DOCKER_TEST_LOG"
  : > "$DOCKER_TEST_LOG"
  case "$capacity_case" in
    cpu) export DOCKER_TEST_CPU_COUNT=0 ;;
    memory) export DOCKER_TEST_MEM_AVAILABLE_KB=$((700 * 1024)) ;;
    disk) export DOCKER_TEST_DISK_AVAILABLE_MIB=512 ;;
  esac
  if run_candidate_with_resources > "$temporary_directory/capacity-$capacity_case.log" 2>&1; then
    echo "O launcher aceitou capacidade insuficiente de $capacity_case." >&2
    exit 1
  fi
  ! grep -q '^load ' "$DOCKER_TEST_LOG"
  case "$capacity_case" in
    cpu) grep -q 'limite de CPU da candidata somado' "$temporary_directory/capacity-$capacity_case.log" ;;
    memory) grep -q 'memória disponível não comporta' "$temporary_directory/capacity-$capacity_case.log" ;;
    disk) grep -q 'espaço livre no Docker não comporta' "$temporary_directory/capacity-$capacity_case.log" ;;
  esac
  unset DOCKER_TEST_CPU_COUNT DOCKER_TEST_MEM_AVAILABLE_KB DOCKER_TEST_DISK_AVAILABLE_MIB
done

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  > "$temporary_directory/missing-resource-budget.log" 2>&1; then
  echo 'O launcher aceitou iniciar sem orçamento explícito de recursos.' >&2
  exit 1
fi
grep -q -- '--candidate-cpus' "$temporary_directory/missing-resource-budget.log"
[[ ! -s "$DOCKER_TEST_LOG" ]]

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --active-overlap \
  --legacy-api-origin http://previous-api:8080 \
  > "$temporary_directory/active.log"
grep -q 'Modo ativo de convivência explicitamente solicitado' "$temporary_directory/active.log"
grep -q -- '--env Deployment__PassiveMode=false' "$DOCKER_TEST_LOG"
grep -q -- '--env Telemetry__LegacyApiBaseUrl=http://previous-api:8080' "$DOCKER_TEST_LOG"
! grep -q '^exec ' "$DOCKER_TEST_LOG"

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_PASSIVE_APPLICATION_STATUS=200
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-route-failure.log" 2>&1; then
  echo 'O launcher aceitou rota comercial habilitada em modo passivo.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q 'respondeu fora de 503 em /api/machines' "$temporary_directory/passive-route-failure.log"
unset DOCKER_TEST_PASSIVE_APPLICATION_STATUS

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_PASSIVE_DEPLOY_READY_STATUS=200
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-deploy-ready-failure.log" 2>&1; then
  echo 'O launcher aceitou readiness de deploy liberada em modo passivo.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q 'respondeu fora de 503 em /health/deploy-ready' "$temporary_directory/passive-deploy-ready-failure.log"
unset DOCKER_TEST_PASSIVE_DEPLOY_READY_STATUS

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_PASSIVE_READY_STATUS=503
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-readiness-failure.log" 2>&1; then
  echo 'O launcher aceitou readiness de banco/migrations bloqueada em modo passivo.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q 'não retornou 200 em /health/ready' "$temporary_directory/passive-readiness-failure.log"
! grep -q '^load ' "$DOCKER_TEST_LOG"
! grep -q '/health/deploy-ready 503' "$DOCKER_TEST_LOG"
! grep -q '/api/machines 503' "$DOCKER_TEST_LOG"
unset DOCKER_TEST_PASSIVE_READY_STATUS

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_PASSIVE_MODE=false
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-mode-failure.log" 2>&1; then
  echo 'O launcher aceitou Deployment__PassiveMode diferente de true.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q 'não confirmou Deployment__PassiveMode=true' "$temporary_directory/passive-mode-failure.log"
unset DOCKER_TEST_PASSIVE_MODE

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_PASSIVE_MODE=false
export DOCKER_TEST_STOP_FAIL=1
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-kill-fallback.log" 2>&1; then
  echo 'O launcher informou sucesso após ativar o fallback de parada.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q '^kill ' "$DOCKER_TEST_LOG"
grep -q 'container candidato foi encerrado' "$temporary_directory/passive-kill-fallback.log"

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_KILL_FAIL=1
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  > "$temporary_directory/passive-stop-failure.log" 2>&1; then
  echo 'O launcher informou sucesso mesmo sem confirmar a parada da candidata passiva.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q '^kill ' "$DOCKER_TEST_LOG"
grep -q 'falharam a parada graciosa e o encerramento forçado' "$temporary_directory/passive-stop-failure.log"
unset DOCKER_TEST_PASSIVE_MODE DOCKER_TEST_STOP_FAIL DOCKER_TEST_KILL_FAIL

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
printf 'Deployment__PassiveMode=false\n' >> "$temporary_directory/runtime.env"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou que o arquivo runtime sobrescrevesse o modo passivo.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]
sed -i '/^Deployment__PassiveMode=/d' "$temporary_directory/runtime.env"

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
printf '%s\n' "$(printf 'c%.0s' {1..40})" > "$release_directory/source-sha.txt"
(cd "$release_directory" && sha256sum source-sha.txt postgresql-migration-test.sql > SHA256SUMS)
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou migrations de outro SHA.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]
(cd "$release_directory" && printf '%s\n' "$source_sha" > source-sha.txt && sha256sum source-sha.txt postgresql-migration-test.sql > SHA256SUMS)

chmod 644 "$temporary_directory/runtime.env"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou um arquivo de runtime legível por grupo/outros.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]
chmod 600 "$temporary_directory/runtime.env"

mkdir "$temporary_directory/artifact-alias"
ln -s "$artifact_directory" "$temporary_directory/artifact-alias/nested"
printf 'ASPNETCORE_ENVIRONMENT=Production\nDOTNET_ENVIRONMENT=Production\n' > "$artifact_directory/secret-runtime.env"
chmod 600 "$artifact_directory/secret-runtime.env"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/artifact-alias/nested/secret-runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou runtime dentro do artefato via diretório simbólico.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]
rm -f "$artifact_directory/secret-runtime.env" "$temporary_directory/artifact-alias/nested"
rmdir "$temporary_directory/artifact-alias"

mkdir "$temporary_directory/release-alias"
ln -s "$release_directory" "$temporary_directory/release-alias/nested"
printf 'ASPNETCORE_ENVIRONMENT=Production\nDOTNET_ENVIRONMENT=Production\n' > "$release_directory/secret-runtime.env"
chmod 600 "$release_directory/secret-runtime.env"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/release-alias/nested/secret-runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou runtime dentro do pacote operacional via diretório simbólico.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]
rm -f "$release_directory/secret-runtime.env" "$temporary_directory/release-alias/nested"
rmdir "$temporary_directory/release-alias"

if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://api.vendmachine.com.br:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou a origem pública da API.' >&2
  exit 1
fi
[[ ! -s "$DOCKER_TEST_LOG" ]]

printf 'alteração\n' >> "$artifact_directory/backend-image.tar.gz"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou um artefato com checksum inválido.' >&2
  exit 1
fi
! grep -q '^load ' "$DOCKER_TEST_LOG"

write_image_archive
(cd "$artifact_directory" && sha256sum backend-image.tar.gz image-id.txt image-ref.txt platform.txt source-sha.txt README.txt Verify-BackendImageArtifact.ps1 start-private-backend-candidate.sh verify-candidate-artifact-pair.py > SHA256SUMS)
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_HEALTH_STATUS=unhealthy
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --candidate-cpus 0.25 \
  --candidate-memory-mib 256 \
  --reserve-host-cpus 0.25 \
  --reserve-host-memory-mib 512 \
  --reserve-docker-disk-mib 512 \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou health check unhealthy.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"

echo 'Private backend candidate launcher: 2 inícios (passivo padrão e ativo explícito), orçamento CPU/memória/disco exigido, insuficiência recusada antes de carregar imagem, readiness e rotas passivas, parada segura/fallback verificados.'
