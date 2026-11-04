#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import tempfile
import unittest
from pathlib import Path

import assemble_release_package as package_builder


class AssembleReleasePackageTests(unittest.TestCase):
    def create_fixture(self, root: Path, segment_count: int = 7) -> tuple[Path, Path]:
        repository = root / "repository"
        segments = root / "segments"
        repository.mkdir()
        segments.mkdir()
        for relative_path in package_builder.RELEASE_FILES:
            source = repository / relative_path
            source.parent.mkdir(parents=True, exist_ok=True)
            contents = "runbook references [preflight](deploy-preflight.sql)\n" if source.name == "deploy-seguro.md" else f"fixture: {source.name}\n"
            source.write_text(contents, encoding="utf-8")
        for name in package_builder.MIGRATION_FILES[:segment_count]:
            (segments / name).write_text(f"-- {name}\n", encoding="utf-8")
        return repository, segments

    def test_creates_sha_matched_package_with_seven_segments_and_checksums(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root)
            package = root / "package"
            source_sha = "a" * 40

            package_builder.assemble_release_package(repository, segments, package, source_sha)

            self.assertEqual(source_sha, (package / "source-sha.txt").read_text(encoding="utf-8").strip())
            self.assertTrue((package / "Validate-TelemetryOverlapReadiness.py").is_file())
            self.assertTrue(all((package / name).is_file() for name in package_builder.MIGRATION_FILES))
            self.assertEqual(7, len(list(package.glob("postgresql-migration*.sql"))))
            for line in (package / "SHA256SUMS").read_text(encoding="ascii").splitlines():
                digest, filename = line.split("  ", 1)
                self.assertEqual(digest, hashlib.sha256((package / filename).read_bytes()).hexdigest())

    def test_release_package_inventory_covers_repository_runbook_links(self) -> None:
        repository = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            segments = root / "segments"
            segments.mkdir()
            for index, name in enumerate(package_builder.MIGRATION_FILES):
                (segments / name).write_text(f"-- synthetic segment {index}\n", encoding="utf-8")

            package = package_builder.assemble_release_package(
                repository, segments, root / "package", "f" * 40
            )

            self.assertTrue((package / "partial-production-rollout.md").is_file())
            self.assertTrue((package / "partial-production-evidence-template.md").is_file())
            self.assertTrue((package / "telemetry-continuity-architecture.md").is_file())
            self.assertTrue((package / "Validate-TelemetryOverlapReadiness.py").is_file())

    def test_rejects_invalid_source_sha_before_creating_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root)
            package = root / "package"

            with self.assertRaisesRegex(ValueError, "40 caracteres"):
                package_builder.assemble_release_package(repository, segments, package, "abc")

            self.assertFalse(package.exists())

    def test_rejects_missing_migration_segment(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root, segment_count=6)
            package = root / "package"

            with self.assertRaisesRegex(ValueError, "Segmentos PostgreSQL ausentes"):
                package_builder.assemble_release_package(repository, segments, package, "b" * 40)

            self.assertFalse(package.exists())

    def test_rejects_unexpected_migration_segment(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root)
            (segments / "postgresql-migration-unreviewed.sql").write_text("-- unexpected\n", encoding="utf-8")
            package = root / "package"

            with self.assertRaisesRegex(ValueError, "inesperados"):
                package_builder.assemble_release_package(repository, segments, package, "c" * 40)

            self.assertFalse(package.exists())

    def test_rejects_nonempty_output_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root)
            package = root / "package"
            package.mkdir()
            (package / "stale.sql").write_text("stale", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "precisa estar vazio"):
                package_builder.assemble_release_package(repository, segments, package, "d" * 40)

            self.assertEqual("stale", (package / "stale.sql").read_text(encoding="utf-8"))

    def test_rejects_unresolved_runbook_link_without_returning_a_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            repository, segments = self.create_fixture(root)
            (repository / "docs/deploy-seguro.md").write_text("[ausente](not-in-package.sql)\n", encoding="utf-8")
            package = root / "package"

            with self.assertRaisesRegex(ValueError, "Link do runbook"):
                package_builder.assemble_release_package(repository, segments, package, "e" * 40)

            self.assertFalse((package / "SHA256SUMS").exists())


if __name__ == "__main__":
    unittest.main()
