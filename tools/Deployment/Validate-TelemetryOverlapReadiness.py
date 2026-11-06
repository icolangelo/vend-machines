#!/usr/bin/env python3
"""Read-only preflight for a candidate API sharing the database with legacy MDB sockets."""

from __future__ import annotations

import argparse
import getpass
import json
import os
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class RejectRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


class PreflightError(RuntimeError):
    pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Confere somente leitura a prontidão da API candidata e a convivência "
            "com sockets MDB ainda pertencentes à API anterior."
        )
    )
    parser.add_argument(
        "--api-base-url",
        required=True,
        help="Origem acessível da API candidata, sem caminho, query ou fragmento.",
    )
    parser.add_argument(
        "--expected-commit",
        required=True,
        help="SHA esperado no campo sourceCommit dos endpoints de readiness.",
    )
    parser.add_argument(
        "--minimum-legacy-connections",
        type=int,
        default=1,
        help="Mínimo de conexões legadas online que precisam ser observadas (padrão: 1).",
    )
    parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=8.0,
        help="Timeout por requisição HTTP (padrão: 8 segundos).",
    )
    return parser.parse_args()


def normalize_base_url(value: str) -> str:
    parsed = urlsplit(value)
    if (
        parsed.scheme not in {"http", "https"}
        or not parsed.netloc
        or parsed.username is not None
        or parsed.password is not None
        or parsed.path not in {"", "/"}
        or parsed.query
        or parsed.fragment
    ):
        raise PreflightError(
            "--api-base-url deve ser uma origem HTTP(S), sem credenciais, caminho, query ou fragmento."
        )
    return urlunsplit((parsed.scheme, parsed.netloc, "", "", ""))


def read_json(opener, url: str, token: str | None, timeout: float) -> dict:
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = Request(url, headers=headers, method="GET")
    try:
        with opener.open(request, timeout=timeout) as response:
            if response.status != 200:
                raise PreflightError(f"HTTP {response.status} em {urlsplit(url).path}.")
            payload = json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        raise PreflightError(f"HTTP {exc.code} em {urlsplit(url).path}; redirects não são seguidos.") from None
    except (URLError, TimeoutError, OSError) as exc:
        raise PreflightError(
            f"Falha de rede em {urlsplit(url).path}: {type(exc).__name__}."
        ) from None
    except json.JSONDecodeError:
        raise PreflightError(f"Resposta inválida em JSON em {urlsplit(url).path}.") from None
    if not isinstance(payload, dict):
        raise PreflightError(f"Formato inesperado em {urlsplit(url).path}.")
    return payload


def require_status(payload: dict, endpoint: str, expected: str) -> None:
    actual = payload.get("status")
    if actual != expected:
        reason = payload.get("reason")
        detail = f" (reason={reason})" if isinstance(reason, str) else ""
        raise PreflightError(f"{endpoint}: status {actual!r}; esperado {expected!r}{detail}.")


def check_commit(payload: dict, endpoint: str, expected: str) -> None:
    actual = payload.get("sourceCommit")
    if not isinstance(actual, str) or actual.lower() != expected.lower():
        raise PreflightError(f"{endpoint}: sourceCommit não corresponde ao SHA esperado.")


def main() -> int:
    args = parse_args()
    if args.minimum_legacy_connections < 1:
        print("ERRO: --minimum-legacy-connections deve ser pelo menos 1 neste preflight.", file=sys.stderr)
        return 2
    if args.timeout_seconds <= 0:
        print("ERRO: --timeout-seconds deve ser positivo.", file=sys.stderr)
        return 2
    if re.fullmatch(r"[0-9a-fA-F]{40}", args.expected_commit) is None:
        print("ERRO: --expected-commit deve ser um SHA Git completo de 40 caracteres hexadecimais.", file=sys.stderr)
        return 2

    token = os.environ.get("VENDMACHINES_ADMIN_BEARER_TOKEN", "").strip()
    if not token:
        try:
            token = getpass.getpass("Token bearer de administrador de plataforma (entrada oculta): ").strip()
        except (EOFError, OSError):
            token = ""
        if not token:
            print("ERRO: informe o token pelo prompt oculto ou em VENDMACHINES_ADMIN_BEARER_TOKEN.", file=sys.stderr)
            return 2

    try:
        base_url = normalize_base_url(args.api_base_url)
        opener = build_opener(RejectRedirects())

        live = read_json(opener, f"{base_url}/health", None, args.timeout_seconds)
        require_status(live, "/health", "Healthy")
        ready = read_json(opener, f"{base_url}/health/ready", None, args.timeout_seconds)
        require_status(ready, "/health/ready", "Ready")
        check_commit(ready, "/health/ready", args.expected_commit)
        deploy_ready = read_json(opener, f"{base_url}/health/deploy-ready", None, args.timeout_seconds)
        require_status(deploy_ready, "/health/deploy-ready", "Ready")
        check_commit(deploy_ready, "/health/deploy-ready", args.expected_commit)

        legacy = read_json(
            opener,
            f"{base_url}/api/admin/legacy-machine-connections",
            token,
            args.timeout_seconds,
        )
        legacy_count = legacy.get("totalItems")
        relay = legacy.get("relay")
        if not isinstance(legacy_count, int) or not isinstance(relay, dict):
            raise PreflightError("/api/admin/legacy-machine-connections: formato inesperado.")
        if legacy_count < args.minimum_legacy_connections:
            raise PreflightError(
                f"Foram observadas {legacy_count} conexões legadas; mínimo exigido: "
                f"{args.minimum_legacy_connections}."
            )
        if relay.get("configured") is not True or relay.get("healthy") is not True:
            raise PreflightError("O relay legado não está configurado e saudável na candidata.")

        worker_health = read_json(
            opener,
            f"{base_url}/api/admin/worker-health",
            token,
            args.timeout_seconds,
        )
        queue_metrics = worker_health.get("queueMetrics")
        machine_connections = queue_metrics.get("machineConnections") if isinstance(queue_metrics, dict) else None
        legacy_relay = machine_connections.get("legacyRelay") if isinstance(machine_connections, dict) else None
        readiness_gate = machine_connections.get("readinessGate") if isinstance(machine_connections, dict) else None
        if not isinstance(legacy_relay, dict) or not isinstance(readiness_gate, dict):
            raise PreflightError("/api/admin/worker-health: métricas de conexão ausentes.")
        if legacy_relay.get("eligibleConnections") != legacy_count or legacy_relay.get("healthy") is not True:
            raise PreflightError("As métricas do relay divergem da lista de conexões legadas.")
        if readiness_gate.get("unroutable") != 0:
            raise PreflightError("A candidata reporta conexões online sem rota compatível.")

        print("overlap_runtime_preflight=passed")
        print(f"candidate_commit={ready.get('sourceCommit', 'not-reported')}")
        print(f"legacy_connections_preserved={legacy_count}")
        print("legacy_owner_relay=healthy")
        print("candidate_unroutable_connections=0")
        return 0
    except PreflightError as exc:
        print(f"overlap_runtime_preflight=blocked: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
