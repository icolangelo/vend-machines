#!/usr/bin/env python3
"""Resolve the API commit currently serving production from its readiness endpoint."""

from __future__ import annotations

import argparse
import json
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener

COMMIT_PATTERN = re.compile(r"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\Z")


class NoRedirectHandler(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def validate_health_url(health_url: str, deploy_ready_url: str) -> None:
    parsed = urlparse(health_url)
    deploy_ready = urlparse(deploy_ready_url)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or parsed.path.rstrip("/") != "/health/ready"
    ):
        raise ValueError("A URL da API de produção precisa ser HTTPS e terminar em /health/ready.")
    if (
        deploy_ready.scheme != "https"
        or deploy_ready.netloc != parsed.netloc
        or deploy_ready.username is not None
        or deploy_ready.password is not None
        or deploy_ready.query
        or deploy_ready.fragment
        or deploy_ready.path.rstrip("/") != "/health/deploy-ready"
    ):
        raise ValueError("A URL de deploy readiness precisa ser HTTPS no mesmo host e terminar em /health/deploy-ready.")


def validate_commit(commit: str | None, source: str) -> str:
    if not isinstance(commit, str) or not COMMIT_PATTERN.fullmatch(commit):
        raise ValueError(f"A revisão {source} não contém um SHA completo de commit.")
    return commit.lower()


def _read_status_and_payload(url: str, open_url):
    request = Request(url, headers={"Accept": "application/json", "User-Agent": "vend-machines-release-check"})
    try:
        with open_url(request, timeout=10) as response:
            body = response.read(16_384)
            return response.status, json.loads(body) if response.status == 200 else None
    except HTTPError as error:
        return error.code, None
    except URLError as error:
        raise ValueError("Não foi possível consultar a readiness da API de produção.") from error


def resolve_revision(health_url: str, deploy_ready_url: str, bootstrap_ref: str, open_url=None) -> tuple[str, str]:
    validate_health_url(health_url, deploy_ready_url)
    bootstrap_sha = validate_commit(bootstrap_ref, "de bootstrap")
    if open_url is None:
        opener = build_opener(NoRedirectHandler())
        open_url = opener.open

    status, payload = _read_status_and_payload(health_url, open_url)
    if status == 404:
        deploy_ready_status, _ = _read_status_and_payload(deploy_ready_url, open_url)
        if deploy_ready_status == 404:
            return bootstrap_sha, "bootstrap"
        raise ValueError(
            f"/health/ready respondeu HTTP 404, mas /health/deploy-ready respondeu HTTP {deploy_ready_status}; "
            "bootstrap parcial bloqueado."
        )
    if status != 200:
        raise ValueError(f"/health/ready respondeu HTTP {status}; a compatibilidade não pode ser verificada.")

    if not isinstance(payload, dict) or payload.get("status") != "Ready":
        state = payload.get("status") if isinstance(payload, dict) else "resposta inválida"
        raise ValueError(f"A API de produção não está Ready ({state}); a publicação foi bloqueada.")

    return validate_commit(payload.get("sourceCommit"), "sourceCommit de produção"), "live"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--health-url", required=True)
    parser.add_argument("--deploy-ready-url", required=True)
    parser.add_argument("--bootstrap-ref", required=True)
    args = parser.parse_args()

    try:
        revision, source = resolve_revision(args.health_url, args.deploy_ready_url, args.bootstrap_ref)
    except (ValueError, json.JSONDecodeError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(f"Revisão da API resolvida por {source}: {revision}", file=sys.stderr)
    print(revision)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
