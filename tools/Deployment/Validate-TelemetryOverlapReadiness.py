#!/usr/bin/env python3
"""Read-only preflight for a candidate API sharing the database with legacy MDB sockets."""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import math
import os
import re
import sys
import time
from datetime import datetime, timezone
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
from uuid import UUID


class RejectRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


class PreflightError(RuntimeError):
    pass


LEGACY_CONNECTION_FRESHNESS_SECONDS = 90.0


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
        required=True,
        help="Mínimo de conexões legadas online, informado conforme a frota em operação.",
    )
    parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=8.0,
        help="Timeout por requisição HTTP (padrão: 8 segundos).",
    )
    parser.add_argument(
        "--maximum-clock-skew-seconds",
        type=float,
        default=30.0,
        help="Desvio máximo entre o relógio local e /health.timestamp (padrão: 30 segundos).",
    )
    parser.add_argument(
        "--maximum-last-seen-age-seconds",
        type=float,
        default=75.0,
        help="Idade máxima de LastSeenAt em cada amostra (padrão: 75 segundos).",
    )
    parser.add_argument(
        "--observation-seconds",
        type=float,
        default=0.0,
        help="Janela de observação repetida; zero executa uma única amostra (padrão: 0).",
    )
    parser.add_argument(
        "--sample-interval-seconds",
        type=float,
        default=15.0,
        help="Intervalo entre amostras durante a janela (padrão: 15 segundos).",
    )
    return parser.parse_args()


def normalize_base_url(value: str) -> str:
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except ValueError:
        raise PreflightError("--api-base-url contém host ou porta inválidos.") from None
    if (
        parsed.scheme not in {"http", "https"}
        or not parsed.netloc
        or not parsed.hostname
        or (port is not None and port < 1)
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
    except (UnicodeDecodeError, json.JSONDecodeError):
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


def parse_utc_timestamp(raw_timestamp: object, endpoint: str, field: str) -> datetime:
    if not isinstance(raw_timestamp, str):
        raise PreflightError(f"{endpoint}: {field} UTC ausente ou inválido.")
    try:
        timestamp = datetime.fromisoformat(raw_timestamp.replace("Z", "+00:00"))
    except ValueError:
        raise PreflightError(f"{endpoint}: {field} UTC ausente ou inválido.") from None
    if timestamp.tzinfo is None:
        raise PreflightError(f"{endpoint}: {field} precisa informar fuso horário.")
    return timestamp.astimezone(timezone.utc)


def check_clock_skew(payload: dict, maximum_skew_seconds: float) -> tuple[float, datetime]:
    raw_timestamp = payload.get("timestamp")
    server_time = parse_utc_timestamp(raw_timestamp, "/health", "timestamp")
    skew_seconds = (server_time - datetime.now(timezone.utc)).total_seconds()
    if abs(skew_seconds) > maximum_skew_seconds:
        raise PreflightError(
            f"/health: diferença entre o relógio da API e o relógio local é de "
            f"{skew_seconds:+.1f}s; máximo permitido: {maximum_skew_seconds:g}s. "
            "Sincronize os relógios UTC antes do preflight."
        )
    return skew_seconds, server_time


def read_legacy_connection_snapshot(
    opener, base_url: str, token: str, timeout: float
) -> tuple[int, dict[str, datetime], dict]:
    endpoint = f"{base_url}/api/admin/legacy-machine-connections"
    page_size = 200
    first = read_json(opener, f"{endpoint}?{urlencode({'page': 1, 'pageSize': page_size})}", token, timeout)
    total_items = first.get("totalItems")
    total_pages = first.get("totalPages")
    relay = first.get("relay")
    if (
        type(total_items) is not int
        or total_items < 0
        or type(total_pages) is not int
        or total_pages < 1
        or not isinstance(relay, dict)
    ):
        raise PreflightError(f"{endpoint}: formato inesperado.")
    expected_pages = max(1, math.ceil(total_items / page_size))
    if total_pages != expected_pages:
        raise PreflightError(f"{endpoint}: paginação inconsistente.")

    machine_last_seen: dict[str, datetime] = {}
    for page_number in range(1, total_pages + 1):
        page = first if page_number == 1 else read_json(
            opener,
            f"{endpoint}?{urlencode({'page': page_number, 'pageSize': page_size})}",
            token,
            timeout,
        )
        if page.get("totalItems") != total_items or page.get("totalPages") != total_pages:
            raise PreflightError(f"{endpoint}: a contagem mudou durante a leitura paginada.")
        items = page.get("items")
        if not isinstance(items, list):
            raise PreflightError(f"{endpoint}: lista de conexões ausente.")
        for item in items:
            machine_id = item.get("machineId") if isinstance(item, dict) else None
            if not isinstance(machine_id, str):
                raise PreflightError(f"{endpoint}: identificador de máquina ausente.")
            try:
                normalized_machine_id = str(UUID(machine_id))
            except ValueError:
                raise PreflightError(f"{endpoint}: identificador de máquina inválido.") from None
            raw_last_seen = item.get("lastSeenAt")
            if not isinstance(raw_last_seen, str):
                raise PreflightError(f"{endpoint}: LastSeenAt ausente ou inválido.")
            try:
                last_seen = datetime.fromisoformat(raw_last_seen.replace("Z", "+00:00"))
            except ValueError:
                raise PreflightError(f"{endpoint}: LastSeenAt ausente ou inválido.") from None
            if last_seen.tzinfo is None:
                raise PreflightError(f"{endpoint}: LastSeenAt precisa informar fuso horário.")
            machine_last_seen[normalized_machine_id] = last_seen.astimezone(timezone.utc)

    if len(machine_last_seen) != total_items:
        raise PreflightError(f"{endpoint}: IDs ausentes ou duplicados na lista de conexões.")
    return total_items, machine_last_seen, relay


def validate_snapshot(
    opener,
    base_url: str,
    token: str,
    timeout: float,
    expected_commit: str,
    minimum_connections: int,
    maximum_clock_skew_seconds: float,
    maximum_last_seen_age_seconds: float,
) -> tuple[int, float, dict[str, datetime]]:
    live = read_json(opener, f"{base_url}/health", None, timeout)
    require_status(live, "/health", "Healthy")
    clock_skew_seconds, api_time = check_clock_skew(live, maximum_clock_skew_seconds)
    ready = read_json(opener, f"{base_url}/health/ready", None, timeout)
    require_status(ready, "/health/ready", "Ready")
    check_commit(ready, "/health/ready", expected_commit)
    deploy_ready = read_json(opener, f"{base_url}/health/deploy-ready", None, timeout)
    require_status(deploy_ready, "/health/deploy-ready", "Ready")
    check_commit(deploy_ready, "/health/deploy-ready", expected_commit)

    legacy_count, machine_last_seen, relay = read_legacy_connection_snapshot(
        opener, base_url, token, timeout
    )
    if legacy_count < minimum_connections:
        raise PreflightError(
            f"Foram observadas {legacy_count} conexões legadas; mínimo exigido: "
            f"{minimum_connections}."
        )
    for last_seen in machine_last_seen.values():
        last_seen_age = (api_time - last_seen).total_seconds()
        if last_seen_age < -maximum_clock_skew_seconds:
            raise PreflightError("LastSeenAt está no futuro além do desvio de relógio permitido.")
        if last_seen_age > maximum_last_seen_age_seconds:
            raise PreflightError(
                f"LastSeenAt excedeu {maximum_last_seen_age_seconds:g}s em ao menos uma máquina; "
                "a presença da frota não está fresca o bastante."
            )
    if relay.get("configured") is not True or relay.get("healthy") is not True:
        raise PreflightError("O relay legado não está configurado e saudável na candidata.")

    worker_health = read_json(
        opener,
        f"{base_url}/api/admin/worker-health",
        token,
        timeout,
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

    return legacy_count, clock_skew_seconds, machine_last_seen


def main() -> int:
    args = parse_args()
    if args.minimum_legacy_connections < 1:
        print("ERRO: --minimum-legacy-connections deve ser pelo menos 1 neste preflight.", file=sys.stderr)
        return 2
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        print("ERRO: --timeout-seconds deve ser positivo e finito.", file=sys.stderr)
        return 2
    if not math.isfinite(args.maximum_clock_skew_seconds) or args.maximum_clock_skew_seconds <= 0:
        print("ERRO: --maximum-clock-skew-seconds deve ser positivo e finito.", file=sys.stderr)
        return 2
    if not math.isfinite(args.maximum_last_seen_age_seconds) or args.maximum_last_seen_age_seconds <= 0:
        print("ERRO: --maximum-last-seen-age-seconds deve ser positivo e finito.", file=sys.stderr)
        return 2
    if (
        args.observation_seconds > 0
        and args.sample_interval_seconds > LEGACY_CONNECTION_FRESHNESS_SECONDS - args.maximum_last_seen_age_seconds
    ):
        print(
            "ERRO: o intervalo entre amostras precisa deixar margem para detectar "
            "conexões antes de expirar a presença da API.",
            file=sys.stderr,
        )
        return 2
    if not math.isfinite(args.observation_seconds) or args.observation_seconds < 0:
        print("ERRO: --observation-seconds deve ser finito e não negativo.", file=sys.stderr)
        return 2
    if not math.isfinite(args.sample_interval_seconds) or args.sample_interval_seconds <= 0:
        print("ERRO: --sample-interval-seconds deve ser positivo e finito.", file=sys.stderr)
        return 2
    if re.fullmatch(r"[0-9a-fA-F]{40}", args.expected_commit) is None:
        print("ERRO: --expected-commit deve ser um SHA Git completo de 40 caracteres hexadecimais.", file=sys.stderr)
        return 2

    try:
        base_url = normalize_base_url(args.api_base_url)
    except PreflightError as exc:
        print(f"ERRO: {exc}", file=sys.stderr)
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
        opener = build_opener(RejectRedirects())
        deadline = time.monotonic() + args.observation_seconds
        sample_count = 0
        legacy_count = 0
        baseline_machine_last_seen = None
        machines_with_ping_progress: set[str] = set()
        while True:
            legacy_count, clock_skew_seconds, machine_last_seen = validate_snapshot(
                opener,
                base_url,
                token,
                args.timeout_seconds,
                args.expected_commit,
                args.minimum_legacy_connections,
                args.maximum_clock_skew_seconds,
                args.maximum_last_seen_age_seconds,
            )
            if baseline_machine_last_seen is None:
                baseline_machine_last_seen = machine_last_seen
            elif set(machine_last_seen) != set(baseline_machine_last_seen):
                raise PreflightError(
                    "O conjunto de máquinas legadas mudou durante a janela de observação; "
                    "uma conexão pode ter caído ou sido substituída."
                )
            else:
                machines_with_ping_progress.update(
                    machine_id
                    for machine_id, last_seen in machine_last_seen.items()
                    if last_seen > baseline_machine_last_seen[machine_id]
                )
            sample_count += 1
            observed_at = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
            print(
                f"observation_sample={sample_count} observed_at_utc={observed_at} "
                f"api_clock_skew_seconds={clock_skew_seconds:+.1f} "
                f"legacy_connections={legacy_count}"
            )
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(args.sample_interval_seconds, remaining))

        if args.observation_seconds > 0 and baseline_machine_last_seen is not None:
            machines_without_ping_progress = set(baseline_machine_last_seen) - machines_with_ping_progress
            if machines_without_ping_progress:
                raise PreflightError(
                    f"LastSeenAt não avançou durante a janela para "
                    f"{len(machines_without_ping_progress)} conexão(ões); "
                    "não foi possível comprovar pings persistidos de toda a frota."
                )

        machine_fingerprint = hashlib.sha256(
            "\n".join(sorted(baseline_machine_last_seen or {})).encode("ascii")
        ).hexdigest()
        print("overlap_runtime_preflight=passed")
        print(f"candidate_commit={args.expected_commit}")
        print(f"legacy_connections_preserved={legacy_count}")
        print(f"legacy_machine_set_sha256={machine_fingerprint}")
        print(f"observation_samples={sample_count}")
        print(f"observation_seconds={args.observation_seconds:g}")
        print("legacy_owner_relay=healthy")
        print("candidate_unroutable_connections=0")
        return 0
    except PreflightError as exc:
        print(f"overlap_runtime_preflight=blocked: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
