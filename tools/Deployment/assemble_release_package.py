#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import tempfile
from pathlib import Path


RELEASE_FILES = (
    "tools/Database/Pause-SessionAdmission.sql",
    "tools/Database/Resume-SessionAdmission.sql",
    "tools/Database/List-UnresolvedTelemetryCommands.sql",
    "tools/Database/Resolve-TelemetryCommand.sql",
    "tools/Database/Cancel-PendingTelemetryCommand.sql",
    "tools/Database/Resolve-StaleProcessingTelemetryCommand.sql",
    "tools/Database/Rehearse-PostgresMigrations.ps1",
    "docs/deploy-preflight.sql",
    "docs/deploy-seguro.md",
    "docs/backup-restore-rehearsal.md",
    "docs/demo-data-preflight.sql",
    "docs/frontend-release.md",
    "docs/mercadopago-static-integration-preflight.sql",
    "docs/operacao-suporte.md",
    "docs/partial-production-rollout.md",
    "docs/payment-sensitive-data-preflight.sql",
    "docs/payment-sensitive-data-redaction.sql",
    "docs/platform-admin-bootstrap-preflight.sql",
    "docs/registration-identifiers-preflight.sql",
    "docs/remediacao-historico-segredos.md",
    "docs/sales-backfill-preflight.sql",
    "docs/telemetry-continuity-architecture.md",
    "docs/migration-risk-review.json",
)

MIGRATION_FILES = (
    "postgresql-migrations-before-session-index.sql",
    "postgresql-migration-session-index.sql",
    "postgresql-migrations-between-online-indexes.sql",
    "postgresql-migration-payment-reconciliation-index.sql",
    "postgresql-migrations-after-payment-index.sql",
    "postgresql-migration-inventory-mapping-index.sql",
    "postgresql-migrations-after-inventory-index.sql",
)

SOURCE_SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")


def _verify_local_runbook_links(package_dir: Path) -> None:
    runbook = package_dir / "deploy-seguro.md"
    links = re.findall(r"\]\(([^)]+)\)", runbook.read_text(encoding="utf-8"))
    for link in links:
        target = link.split("#", 1)[0]
        if target.endswith((".sql", ".md", ".ps1", ".json")) and not (package_dir / target).is_file():
            raise ValueError(f"Link do runbook sem arquivo no pacote: {target}")


def assemble_release_package(
    repository_root: Path,
    segments_dir: Path,
    package_dir: Path,
    source_sha: str,
) -> Path:
    if not SOURCE_SHA_PATTERN.fullmatch(source_sha):
        raise ValueError("O SHA de origem deve ter exatamente 40 caracteres hexadecimais minúsculos.")

    source_files = [repository_root / relative_path for relative_path in RELEASE_FILES]
    missing_sources = [str(path.relative_to(repository_root)) for path in source_files if not path.is_file()]
    if missing_sources:
        raise ValueError(f"Arquivos obrigatórios ausentes: {', '.join(missing_sources)}")

    migration_paths = [segments_dir / name for name in MIGRATION_FILES]
    missing_migrations = [path.name for path in migration_paths if not path.is_file() or path.stat().st_size == 0]
    unexpected_migrations = sorted(
        path.name for path in segments_dir.glob("postgresql-migration*.sql") if path.name not in MIGRATION_FILES
    )
    if missing_migrations:
        raise ValueError(f"Segmentos PostgreSQL ausentes ou vazios: {', '.join(missing_migrations)}")
    if unexpected_migrations:
        raise ValueError(f"Segmentos PostgreSQL inesperados: {', '.join(unexpected_migrations)}")

    if package_dir.exists() and any(package_dir.iterdir()):
        raise ValueError(f"O diretório de saída precisa estar vazio: {package_dir}")
    package_dir.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix=f".{package_dir.name}-", dir=package_dir.parent) as temporary_directory:
        staged_package = Path(temporary_directory)
        for source in source_files:
            shutil.copy2(source, staged_package / source.name)
        for source in migration_paths:
            shutil.copy2(source, staged_package / source.name)

        (staged_package / "source-sha.txt").write_text(f"{source_sha}\n", encoding="utf-8")
        _verify_local_runbook_links(staged_package)

        checksummed_files = sorted(
            path for path in staged_package.iterdir() if path.is_file() and path.name != "SHA256SUMS"
        )
        checksum_lines = []
        for path in checksummed_files:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            checksum_lines.append(f"{digest}  {path.name}")
        (staged_package / "SHA256SUMS").write_text("\n".join(checksum_lines) + "\n", encoding="ascii")

        if package_dir.exists():
            package_dir.rmdir()
        staged_package.replace(package_dir)
    return package_dir


def main() -> int:
    parser = argparse.ArgumentParser(description="Monta pacote de release verificável associado a um SHA Git.")
    parser.add_argument("--repository-root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--segments-directory", type=Path, required=True)
    parser.add_argument("--package-directory", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()

    try:
        package = assemble_release_package(
            args.repository_root.resolve(),
            args.segments_directory.resolve(),
            args.package_directory.resolve(),
            args.source_sha,
        )
    except (OSError, ValueError) as error:
        parser.error(str(error))

    print(f"Pacote SHA-matched montado: {package}")
    print(f"Arquivos: {sum(1 for path in package.iterdir() if path.is_file())}")
    print(f"SHA de origem: {args.source_sha}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
