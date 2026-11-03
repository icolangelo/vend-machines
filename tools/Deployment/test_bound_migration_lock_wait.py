#!/usr/bin/env python3

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import bound_migration_lock_wait as lock_wait


class BoundMigrationLockWaitTests(unittest.TestCase):
    def create_scripts(self, directory: Path, count: int = 7) -> list[Path]:
        paths = []
        for index in range(count):
            path = directory / f"postgresql-migration-{index}.sql"
            path.write_text(f"-- segment {index}\nBEGIN;\nCOMMIT;\n", encoding="utf-8")
            paths.append(path)
        return paths

    def test_prepends_lock_timeout_to_all_seven_segments(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            paths = self.create_scripts(directory)

            self.assertEqual(paths, lock_wait.add_lock_timeout(directory))

            for index, path in enumerate(paths):
                self.assertTrue(path.read_text(encoding="utf-8").startswith(lock_wait.LOCK_TIMEOUT_PREFIX))
                self.assertIn(f"-- segment {index}", path.read_text(encoding="utf-8"))

    def test_removes_utf8_bom_before_prepending_sql(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            paths = self.create_scripts(directory)
            for path in paths:
                path.write_text(path.read_text(encoding="utf-8"), encoding="utf-8-sig")

            lock_wait.add_lock_timeout(directory)

            for path in paths:
                contents = path.read_bytes()
                self.assertFalse(contents.startswith(b"\xef\xbb\xbf"))
                self.assertTrue(contents.startswith(lock_wait.LOCK_TIMEOUT_PREFIX.encode("utf-8")))
                self.assertIn(b"-- segment", contents)

    def test_rejects_an_incomplete_script_set_without_modifying_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            paths = self.create_scripts(directory, count=4)

            with self.assertRaisesRegex(ValueError, "Esperados 7 scripts"):
                lock_wait.add_lock_timeout(directory)

            self.assertTrue(all(not path.read_text(encoding="utf-8").startswith(lock_wait.LOCK_TIMEOUT_PREFIX) for path in paths))

    def test_rejects_existing_prefix_before_modifying_any_other_script(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            paths = self.create_scripts(directory)
            paths[-1].write_text(lock_wait.LOCK_TIMEOUT_PREFIX + paths[-1].read_text(encoding="utf-8"), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "já inserido"):
                lock_wait.add_lock_timeout(directory)

            self.assertFalse(paths[0].read_text(encoding="utf-8").startswith(lock_wait.LOCK_TIMEOUT_PREFIX))


if __name__ == "__main__":
    unittest.main()
