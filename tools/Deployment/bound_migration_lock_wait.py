#!/usr/bin/env python3
"""Add a fail-fast PostgreSQL lock wait limit to the generated migration scripts."""

from __future__ import annotations

import sys
from pathlib import Path


LOCK_TIMEOUT_PREFIX = "SET lock_timeout = '5s';\n"
EXPECTED_SCRIPT_COUNT = 7


def add_lock_timeout(artifacts_dir: Path) -> list[Path]:
    scripts = sorted(artifacts_dir.glob("postgresql-migration*.sql"))
    if len(scripts) != EXPECTED_SCRIPT_COUNT:
        raise ValueError(
            f"Esperados {EXPECTED_SCRIPT_COUNT} scripts de migration PostgreSQL; encontrados {len(scripts)}."
        )

    # EF's generated script can begin with a UTF-8 BOM. Decode it explicitly so
    # prepending the timeout does not leave U+FEFF before the first SQL token.
    contents = [(path, path.read_text(encoding="utf-8-sig")) for path in scripts]
    already_prefixed = [path.name for path, content in contents if content.startswith(LOCK_TIMEOUT_PREFIX)]
    if already_prefixed:
        names = ", ".join(already_prefixed)
        raise ValueError(f"Timeout de lock já inserido em {names}; não duplicar o preâmbulo.")

    for path, content in contents:
        path.write_text(LOCK_TIMEOUT_PREFIX + content, encoding="utf-8", newline="\n")
    return scripts


def main() -> int:
    artifacts_dir = Path(sys.argv[1]) if len(sys.argv) == 2 else Path("artifacts")
    try:
        scripts = add_lock_timeout(artifacts_dir)
    except (OSError, ValueError) as exc:
        print(f"::error::{exc}", file=sys.stderr)
        return 1
    print(f"Limite lock_timeout=5s inserido nos {len(scripts)} scripts PostgreSQL.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
