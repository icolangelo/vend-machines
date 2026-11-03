from __future__ import annotations

import hashlib
import importlib.util
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("verify-candidate-artifact-pair.py")
SPEC = importlib.util.spec_from_file_location("candidate_artifact_pair", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class CandidateArtifactPairTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.image_directory = self.root / "image"
        self.release_directory = self.root / "release"
        self.image_directory.mkdir()
        self.release_directory.mkdir()
        self.source_sha = "a" * 40
        self._write_image()
        self._write_release()

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def _write_files(self, directory: Path, files: dict[str, bytes]) -> None:
        for name, content in files.items():
            (directory / name).write_bytes(content)
        checksum_lines = [
            f"{hashlib.sha256(content).hexdigest()}  {name}"
            for name, content in sorted(files.items())
        ]
        (directory / "SHA256SUMS").write_text("\n".join(checksum_lines) + "\n", encoding="ascii")

    def _write_image(self) -> None:
        files = {
            "backend-image.tar.gz": b"image-fixture",
            "image-id.txt": f"sha256:{'b' * 64}\n".encode(),
            "image-ref.txt": f"vend-machines-backend:{self.source_sha}\n".encode(),
            "platform.txt": b"linux/amd64\n",
            "source-sha.txt": f"{self.source_sha}\n".encode(),
            "README.txt": b"candidate image\n",
            "Verify-BackendImageArtifact.ps1": b"fixture verifier\n",
            "start-private-backend-candidate.sh": b"fixture launcher\n",
            "verify-candidate-artifact-pair.py": b"fixture pair verifier\n",
        }
        self._write_files(self.image_directory, files)

    def _write_release(self, source_sha: str | None = None) -> None:
        files = {
            "source-sha.txt": f"{source_sha or self.source_sha}\n".encode(),
            "postgresql-migrations-before-session-index.sql": b"SELECT 1;\n",
            "partial-production-rollout.md": b"same-source runbook\n",
        }
        self._write_files(self.release_directory, files)

    def test_accepts_matching_checksums_and_source_revision(self) -> None:
        MODULE.verify_candidate_artifacts(self.image_directory, self.release_directory, self.source_sha)

    def test_rejects_release_package_from_different_revision(self) -> None:
        self._write_release("c" * 40)
        with self.assertRaisesRegex(MODULE.ArtifactValidationError, "não coincidem"):
            MODULE.verify_candidate_artifacts(self.image_directory, self.release_directory, self.source_sha)

    def test_rejects_changed_file_even_when_source_sha_matches(self) -> None:
        (self.release_directory / "postgresql-migrations-before-session-index.sql").write_text(
            "DROP TABLE machines;\n", encoding="utf-8"
        )
        with self.assertRaisesRegex(MODULE.ArtifactValidationError, "Checksum inválido"):
            MODULE.verify_candidate_artifacts(self.image_directory, self.release_directory, self.source_sha)

    def test_rejects_path_traversal_in_manifest(self) -> None:
        manifest = self.release_directory / "SHA256SUMS"
        manifest.write_text(f"{'d' * 64}  ../outside.sql\n", encoding="ascii")
        with self.assertRaisesRegex(MODULE.ArtifactValidationError, "entrada inválida"):
            MODULE.verify_candidate_artifacts(self.image_directory, self.release_directory, self.source_sha)

    def test_rejects_extra_image_files(self) -> None:
        extra = self.image_directory / "unexpected.txt"
        extra.write_text("extra", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ArtifactValidationError, "extras ou incompletos"):
            MODULE.verify_candidate_artifacts(self.image_directory, self.release_directory, self.source_sha)


if __name__ == "__main__":
    unittest.main()
