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
source_sha='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
image_id="sha256:$(printf 'b%.0s' {1..64})"
printf 'fixture-image-bytes\n' > "$artifact_directory/backend-image.tar.gz"
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
  info) printf 'linux/x86_64\n' ;;
  network) exit 0 ;;
  container) exit 1 ;;
  load) exit 0 ;;
  image) printf '%s\n' "$DOCKER_TEST_IMAGE_ID" ;;
  run) printf 'container-id\n' ;;
  inspect) printf '%s\n' "${DOCKER_TEST_HEALTH_STATUS:-healthy}" ;;
  exec)
    route="${7:-}"
    expected_status="${8:-}"
    case "$route" in
      /health/deploy-ready) actual_status="${DOCKER_TEST_PASSIVE_DEPLOY_READY_STATUS:-503}" ;;
      /api/machines|/telemetria) actual_status="${DOCKER_TEST_PASSIVE_APPLICATION_STATUS:-503}" ;;
      *) exit 8 ;;
    esac
    [[ "$actual_status" == "$expected_status" ]]
    ;;
  stop) [[ "${DOCKER_TEST_STOP_FAIL:-0}" != 1 ]] ;;
  *) exit 9 ;;
esac
DOCKER
chmod 755 "$bin_directory/docker"

export PATH="$bin_directory:$PATH"
export DOCKER_TEST_IMAGE_ID="$image_id"
export DOCKER_TEST_LOG="$temporary_directory/docker.log"

bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  > "$temporary_directory/success.log"
grep -q 'Health check aprovado' "$temporary_directory/success.log"
grep -q -- '--publish 127.0.0.1:18080:8080' "$DOCKER_TEST_LOG"
grep -q -- '--env Deployment__PassiveMode=true' "$DOCKER_TEST_LOG"
! grep -q -- 'Telemetry__LegacyApiBaseUrl=' "$DOCKER_TEST_LOG"
! grep -q -- '--restart' "$DOCKER_TEST_LOG"

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
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
  > "$temporary_directory/passive-deploy-ready-failure.log" 2>&1; then
  echo 'O launcher aceitou readiness de deploy liberada em modo passivo.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"
grep -q 'respondeu fora de 503 em /health/deploy-ready' "$temporary_directory/passive-deploy-ready-failure.log"
unset DOCKER_TEST_PASSIVE_DEPLOY_READY_STATUS

rm -f "$DOCKER_TEST_LOG"
: > "$DOCKER_TEST_LOG"
printf 'Deployment__PassiveMode=false\n' >> "$temporary_directory/runtime.env"
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
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
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou um artefato com checksum inválido.' >&2
  exit 1
fi
! grep -q '^load ' "$DOCKER_TEST_LOG"

printf 'fixture-image-bytes\n' > "$artifact_directory/backend-image.tar.gz"
(cd "$artifact_directory" && sha256sum backend-image.tar.gz image-id.txt image-ref.txt platform.txt source-sha.txt README.txt Verify-BackendImageArtifact.ps1 start-private-backend-candidate.sh verify-candidate-artifact-pair.py > SHA256SUMS)
: > "$DOCKER_TEST_LOG"
export DOCKER_TEST_HEALTH_STATUS=unhealthy
if bash "$launcher" \
  --artifact-dir "$artifact_directory" \
  --release-package-dir "$release_directory" \
  --expected-source-sha "$source_sha" \
  --docker-network private-network \
  --runtime-env-file "$temporary_directory/runtime.env" \
  --legacy-api-origin http://previous-api:8080 \
  >/dev/null 2>&1; then
  echo 'O launcher aceitou health check unhealthy.' >&2
  exit 1
fi
grep -q '^stop --time 45 ' "$DOCKER_TEST_LOG"

echo 'Private backend candidate launcher: 2 inícios (passivo padrão e ativo explícito), bloqueios passivos verificados, parada após probe/health falho e 7 recusas antes do início.'
