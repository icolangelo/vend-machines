#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("AssertApiOnlyCandidate.py")
MODULE_SPEC = importlib.util.spec_from_file_location("api_only_candidate", MODULE_PATH)
assert MODULE_SPEC is not None and MODULE_SPEC.loader is not None
API_ONLY = importlib.util.module_from_spec(MODULE_SPEC)
MODULE_SPEC.loader.exec_module(API_ONLY)


def git(repo: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


class ApiOnlyCandidateTests(unittest.TestCase):
    def make_repo(self, directory: str) -> tuple[Path, str]:
        repo = Path(directory)
        git(repo, "init", "-b", "develop")
        git(repo, "config", "user.name", "Synthetic Test")
        git(repo, "config", "user.email", "synthetic@example.invalid")
        frontend = repo / "apps/frontend/src/app.ts"
        frontend.parent.mkdir(parents=True)
        frontend.write_text("export const app = true;\n", encoding="utf-8")
        git(repo, "add", "apps/frontend/src/app.ts")
        git(repo, "commit", "-m", "frontend base")
        base = git(repo, "rev-parse", "HEAD")
        git(repo, "update-ref", "refs/remotes/origin/develop", base)
        return repo, base

    def test_allows_backend_and_release_tool_changes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo, base = self.make_repo(temporary)
            backend = repo / "apps/backend/Api.cs"
            backend.parent.mkdir(parents=True)
            backend.write_text("// backend change\n", encoding="utf-8")
            tool = repo / "tools/Deployment/check.py"
            tool.parent.mkdir(parents=True)
            tool.write_text("# release check\n", encoding="utf-8")
            git(repo, "add", "apps/backend/Api.cs", "tools/Deployment/check.py")
            git(repo, "commit", "-m", "backend candidate")

            self.assertEqual([], API_ONLY.changed_frontend_files(repo, base, "HEAD"))

    def test_rejects_frontend_additions_and_deletions(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo, base = self.make_repo(temporary)
            frontend = repo / "apps/frontend/src/app.ts"
            frontend.unlink()
            added = repo / "apps/frontend/src/new-page.ts"
            added.write_text("export const page = true;\n", encoding="utf-8")
            git(repo, "add", "-A", "apps/frontend")
            git(repo, "commit", "-m", "frontend change")

            self.assertEqual(
                ["apps/frontend/src/app.ts", "apps/frontend/src/new-page.ts"],
                API_ONLY.changed_frontend_files(repo, base, "HEAD"),
            )

    def test_missing_base_ref_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo, _ = self.make_repo(temporary)

            with self.assertRaises(subprocess.CalledProcessError):
                API_ONLY.changed_frontend_files(repo, "origin/missing", "HEAD")


if __name__ == "__main__":
    unittest.main()
