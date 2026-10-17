#!/usr/bin/env python3
"""Validate that a release has a traceable HTTPS continuity-test reference."""

from __future__ import annotations

import sys
from urllib.parse import urlsplit


def is_valid_evidence_url(value: str) -> bool:
    try:
        parsed = urlsplit(value.strip())
    except ValueError:
        return False
    return (
        parsed.scheme == "https"
        and bool(parsed.hostname)
        and parsed.username is None
        and parsed.password is None
        and parsed.path not in ("", "/")
    )


if __name__ == "__main__":
    if len(sys.argv) != 2 or not is_valid_evidence_url(sys.argv[1]):
        print("Informe o link HTTPS do teste de continuidade aprovado.", file=sys.stderr)
        raise SystemExit(1)
    print("Link HTTPS de evidência de continuidade informado.")
