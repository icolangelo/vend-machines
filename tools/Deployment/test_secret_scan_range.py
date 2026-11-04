#!/usr/bin/env python3

from __future__ import annotations

import subprocess
import tempfile
import unittest
import importlib.util
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("Determine-SecretScanRange.py")
MODULE_SPEC = importlib.util.spec_from_file_location("secret_scan_range", MODULE_PATH)
assert MODULE_SPEC is not None and MODULE_SPEC.loader is not None
SECRET_SCAN_RANGE = importlib.util.module_from_spec(MODULE_SPEC)
MODULE_SPEC.loader.exec_module(SECRET_SCAN_RANGE)
determine_range = SECRET_SCAN_RANGE.determine_range


def git(repo_root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(repo_root), *args],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


class SecretScanRangeTests(unittest.TestCase):
    def make_repo(self, directory: str) -> Path:
        repo = Path(directory)
        git(repo, "init", "-b", "main")
        git(repo, "config", "user.name", "Synthetic Test")
        git(repo, "config", "user.email", "synthetic@example.invalid")
        return repo

    def commit(self, repo: Path, filename: str, content: str, message: str) -> str:
        (repo / filename).write_text(content, encoding="utf-8")
        git(repo, "add", filename)
        git(repo, "commit", "-m", message)
        return git(repo, "rev-parse", "HEAD")

    def test_new_branch_scans_every_commit_since_release_base(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = self.make_repo(temporary)
            base = self.commit(repo, "base.txt", "synthetic base", "base")
            git(repo, "update-ref", "refs/remotes/origin/develop", base)
            first = self.commit(repo, "first.txt", "synthetic first", "first branch change")
            head = self.commit(repo, "second.txt", "synthetic second", "second branch change")

            scan_range = determine_range(
                repo, "push", head, "", "0" * 40, "develop")

            self.assertEqual(f"{base}..{head}", scan_range)
            self.assertEqual([first, head], git(repo, "rev-list", "--reverse", scan_range).splitlines())

    def test_new_branch_without_default_ref_scans_full_reachable_history(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = self.make_repo(temporary)
            first = self.commit(repo, "first.txt", "synthetic first", "root")
            head = self.commit(repo, "second.txt", "synthetic second", "second")

            scan_range = determine_range(
                repo, "push", head, "", "0" * 40, "develop")

            self.assertEqual(head, scan_range)
            self.assertEqual([first, head], git(repo, "rev-list", "--reverse", scan_range).splitlines())

    def test_regular_push_scans_the_event_before_range(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = self.make_repo(temporary)
            before = self.commit(repo, "base.txt", "synthetic base", "base")
            head = self.commit(repo, "change.txt", "synthetic change", "change")

            self.assertEqual(
                f"{before}..{head}",
                determine_range(repo, "push", head, "", before, "main"),
            )

    def test_pull_request_scans_base_to_head(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = self.make_repo(temporary)
            base = self.commit(repo, "base.txt", "synthetic base", "base")
            head = self.commit(repo, "change.txt", "synthetic change", "change")

            self.assertEqual(
                f"{base}..{head}",
                determine_range(repo, "pull_request", head, base, "", "main"),
            )

    def test_first_commit_without_parent_scans_the_root_commit(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = self.make_repo(temporary)
            head = self.commit(repo, "root.txt", "synthetic root", "root")

            self.assertEqual(head, determine_range(repo, "workflow_dispatch", head, "", "", "main"))


if __name__ == "__main__":
    unittest.main()
