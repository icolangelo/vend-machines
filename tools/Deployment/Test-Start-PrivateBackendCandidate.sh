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
  --legacy-api-origin http://previous-api:8080 \
  > "$temporary_directory/success.log"
grep -q 'Health check aprovado' "$temporary_directory/success.log"
grep -q -- '--publish 127.0.0.1:18080:8080' "$DOCKER_TEST_LOG"
! grep -q -- '--restart' "$DOCKER_TEST_LOG"

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

echo 'Private backend candidate launcher: 1 início isolado, parada após health falho e 4 recusas antes do início.'
