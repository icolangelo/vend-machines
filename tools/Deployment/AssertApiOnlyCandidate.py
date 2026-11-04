#!/usr/bin/env python3
"""Fail if an API-only candidate changes frontend files from its release base."""

from __future__ import annotations

import argparse
import subprocess
from pathlib import Path


def changed_frontend_files(repo_root: Path, base_ref: str, head_ref: str) -> list[str]:
    for ref in (base_ref, head_ref):
        subprocess.run(
            ["git", "-C", str(repo_root), "rev-parse", "--verify", f"{ref}^{{commit}}"],
            check=True,
            capture_output=True,
            text=True,
        )

    result = subprocess.run(
        [
            "git", "-C", str(repo_root), "diff", "--name-only", "--no-renames",
            f"{base_ref}...{head_ref}", "--", "apps/frontend/",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return [line for line in result.stdout.splitlines() if line]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-ref", required=True)
    parser.add_argument("--head-ref", required=True)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()

    try:
        changed = changed_frontend_files(args.repo_root, args.base_ref, args.head_ref)
    except subprocess.CalledProcessError as exc:
        print(f"Falha ao comparar os refs do candidato API-only: {exc.stderr.strip()}")
        return 2

    if changed:
        print("Candidato API-only altera arquivos de apps/frontend:")
        for path in changed:
            print(f"- {path}")
        return 1

    print("Escopo API-only aprovado: nenhum arquivo de apps/frontend foi alterado.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
