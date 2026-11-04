#!/usr/bin/env python3
"""Verify the image and SQL/runbook artifacts belong to the same source revision."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path


SHA_PATTERN = re.compile(r"^[a-fA-F0-9]{40}$")
HASH_LINE = re.compile(r"^([a-fA-F0-9]{64})  ([^/\\\s]+)$")
IMAGE_FILES = {
    "backend-image.tar.gz",
    "image-id.txt",
    "image-ref.txt",
    "platform.txt",
    "source-sha.txt",
    "README.txt",
    "Verify-BackendImageArtifact.ps1",
    "start-private-backend-candidate.sh",
    "verify-candidate-artifact-pair.py",
}


class ArtifactValidationError(ValueError):
    pass


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8").strip()
    except (OSError, UnicodeError) as error:
        raise ArtifactValidationError(f"Não foi possível ler {path.name}.") from error


def validate_directory(directory: Path, expected_files: set[str] | None = None) -> set[str]:
    if not directory.is_dir() or directory.is_symlink():
        raise ArtifactValidationError("Informe um diretório extraído sem links simbólicos.")

    entries = list(directory.iterdir())
    if any(entry.is_symlink() or not entry.is_file() for entry in entries):
        raise ArtifactValidationError("O pacote contém link simbólico, pasta ou arquivo não regular.")

    names = {entry.name for entry in entries}
    if len(names) != len(entries) or "SHA256SUMS" not in names:
        raise ArtifactValidationError("O pacote contém nomes duplicados ou não possui SHA256SUMS.")

    if expected_files is not None and names != expected_files | {"SHA256SUMS"}:
        raise ArtifactValidationError("O artefato de imagem contém arquivos extras ou incompletos.")

    manifest_path = directory / "SHA256SUMS"
    try:
        lines = manifest_path.read_text(encoding="ascii").splitlines()
    except (OSError, UnicodeError) as error:
        raise ArtifactValidationError("SHA256SUMS não pôde ser lido como ASCII.") from error

    manifest: dict[str, str] = {}
    for line in lines:
        match = HASH_LINE.fullmatch(line)
        if match is None:
            raise ArtifactValidationError("SHA256SUMS contém uma entrada inválida.")
        digest, filename = match.groups()
        if filename == "SHA256SUMS" or filename in manifest:
            raise ArtifactValidationError("SHA256SUMS contém entrada própria ou nome duplicado.")
        manifest[filename] = digest.lower()

    actual_names = names - {"SHA256SUMS"}
    if set(manifest) != actual_names:
        raise ArtifactValidationError("SHA256SUMS não cobre exatamente os arquivos do pacote.")

    for filename, expected_digest in manifest.items():
        path = directory / filename
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != expected_digest:
            raise ArtifactValidationError(f"Checksum inválido para {filename}.")

    return names


def verify_candidate_artifacts(
    image_directory: Path,
    release_directory: Path,
    expected_source_sha: str,
) -> None:
    if not SHA_PATTERN.fullmatch(expected_source_sha):
        raise ArtifactValidationError("O SHA esperado precisa conter 40 caracteres hexadecimais.")

    if image_directory.is_symlink() or release_directory.is_symlink():
        raise ArtifactValidationError("Informe diretórios de artefato sem links simbólicos.")
    image_directory = image_directory.resolve(strict=True)
    release_directory = release_directory.resolve(strict=True)
    if image_directory == release_directory:
        raise ArtifactValidationError("Imagem e pacote operacional precisam estar em diretórios distintos.")

    validate_directory(image_directory, IMAGE_FILES)
    validate_directory(release_directory)

    expected = expected_source_sha.lower()
    image_sha = read_text(image_directory / "source-sha.txt").lower()
    release_sha = read_text(release_directory / "source-sha.txt").lower()
    if image_sha != expected or release_sha != expected:
        raise ArtifactValidationError("SHA da imagem, pacote operacional e SHA esperado não coincidem.")

    expected_ref = f"vend-machines-backend:{expected}"
    if read_text(image_directory / "image-ref.txt") != expected_ref:
        raise ArtifactValidationError("A referência da imagem não corresponde ao SHA esperado.")

    if re.fullmatch(r"sha256:[a-fA-F0-9]{64}", read_text(image_directory / "image-id.txt")) is None:
        raise ArtifactValidationError("O ID da imagem está ausente ou malformado.")

    if re.fullmatch(r"linux/(amd64|arm64)", read_text(image_directory / "platform.txt").lower()) is None:
        raise ArtifactValidationError("A plataforma declarada pela imagem não é suportada.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image-artifact-dir", type=Path, required=True)
    parser.add_argument("--release-package-dir", type=Path, required=True)
    parser.add_argument("--expected-source-sha", required=True)
    args = parser.parse_args()

    try:
        verify_candidate_artifacts(
            args.image_artifact_dir,
            args.release_package_dir,
            args.expected_source_sha,
        )
    except (ArtifactValidationError, OSError) as error:
        print(f"Artefatos recusados: {error}", file=sys.stderr)
        return 1

    print(f"Artefatos conferem para o SHA {args.expected_source_sha}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
