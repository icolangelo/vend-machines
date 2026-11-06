#!/usr/bin/env python3
"""Validate that a release has a traceable HTTPS continuity-test reference."""

from __future__ import annotations

import os
import re
import sys
from urllib.parse import urlsplit


def is_valid_evidence_url(value: str, repository: str) -> bool:
    owner, separator, name = repository.partition("/")
    if not separator or not owner or not name or "/" in name:
        return False
    try:
        parsed = urlsplit(value.strip())
        port = parsed.port
    except ValueError:
        return False
    return (
        parsed.scheme == "https"
        and parsed.hostname is not None
        and parsed.hostname.lower() == "github.com"
        and port in (None, 443)
        and parsed.username is None
        and parsed.password is None
        and parsed.query == ""
        and parsed.fragment == ""
        and re.fullmatch(
            rf"/{re.escape(owner)}/{re.escape(name)}/(?:actions/runs/[1-9][0-9]*|pull/[1-9][0-9]*)/?",
            parsed.path,
        ) is not None
    )


if __name__ == "__main__":
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if len(sys.argv) != 2 or not is_valid_evidence_url(sys.argv[1], repository):
        print(
            "Informe o link HTTPS de um PR ou de uma execução do GitHub Actions deste repositório.",
            file=sys.stderr,
        )
        raise SystemExit(1)
    print("Link da evidência de continuidade vinculado a este repositório.")
