#!/usr/bin/env python3
"""Update the GitHub compatibility reference after a verified production API deploy."""

from __future__ import annotations

import json
import os
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

COMMIT_PATTERN = re.compile(r"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\Z")
REPOSITORY_PATTERN = re.compile(r"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
VARIABLE_NAME = "PRODUCTION_BACKEND_REF"
GITHUB_API = "https://api.github.com"


def update_repository_variable(repository: str, token: str, revision: str, send_request=None) -> None:
    if not REPOSITORY_PATTERN.fullmatch(repository):
        raise ValueError("GITHUB_REPOSITORY precisa estar no formato owner/repository.")
    if not token.strip():
        raise ValueError("GITHUB_TOKEN não foi fornecido para atualizar a variável do repositório.")
    if not COMMIT_PATTERN.fullmatch(revision):
        raise ValueError("A revisão implantada precisa ser um SHA completo de commit.")
    if send_request is None:
        send_request = urlopen

    variables_url = f"{GITHUB_API}/repos/{repository}/actions/variables"
    variable_url = f"{variables_url}/{VARIABLE_NAME}"
    headers = {
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2022-11-28",
        "Content-Type": "application/json",
        "User-Agent": "vend-machines-production-release",
    }

    def call(method: str, url: str, body: dict | None = None, missing_ok: bool = False) -> tuple[int, bytes]:
        request = Request(
            url,
            data=json.dumps(body).encode("utf-8") if body is not None else None,
            headers=headers,
            method=method,
        )
        try:
            with send_request(request, timeout=15) as response:
                return response.status, response.read(16_384)
        except HTTPError as error:
            if missing_ok and error.code == 404:
                return error.code, b""
            raise ValueError(f"GitHub API retornou HTTP {error.code} ao atualizar {VARIABLE_NAME}.") from error
        except URLError as error:
            raise ValueError(f"Não foi possível acessar a GitHub API para atualizar {VARIABLE_NAME}.") from error

    status, content = call("GET", variable_url, missing_ok=True)
    payload = json.loads(content) if content else {}
    if status == 404:
        call("POST", variables_url, {"name": VARIABLE_NAME, "value": revision})
    elif status == 200:
        if payload.get("value") != revision:
            call("PATCH", variable_url, {"value": revision})
    else:
        raise ValueError(f"GitHub API retornou HTTP {status} ao ler {VARIABLE_NAME}.")

    verify_status, verify_content = call("GET", variable_url)
    if verify_status != 200:
        raise ValueError(f"GitHub API não confirmou a leitura de {VARIABLE_NAME} após a atualização.")
    verified = json.loads(verify_content).get("value")
    if verified != revision:
        raise ValueError(f"GitHub API não confirmou a revisão implantada em {VARIABLE_NAME}.")


def main() -> int:
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    token = os.environ.get("GITHUB_TOKEN", "")
    revision = os.environ.get("DEPLOYED_API_REVISION", "")
    try:
        update_repository_variable(repository, token, revision)
    except (ValueError, json.JSONDecodeError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(f"{VARIABLE_NAME} foi atualizado e confirmado para a revisão implantada {revision.lower()}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
