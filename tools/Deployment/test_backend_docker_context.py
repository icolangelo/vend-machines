#!/usr/bin/env python3
"""Prevent local SQLite databases from entering the backend image context."""

from __future__ import annotations

import subprocess
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CONTEXT = Path("apps/backend/VendingMachines.Api")
REQUIRED_DOCKERIGNORE_PATTERNS = {
    "*.db",
    "*.db-shm",
    "*.db-wal",
    "*.sqlite",
    "*.sqlite-shm",
    "*.sqlite-wal",
    "*.sqlite-*",
    "**/*.db",
    "**/*.db-*",
    "**/*.sqlite",
    "**/*.sqlite-*",
}
SENSITIVE_FIELD_NAMES = {
    "accesstoken",
    "apikey",
    "clientsecret",
    "connectionstring",
    "defaultconnection",
    "legacykey",
    "password",
    "passwd",
    "privatekey",
    "refreshtoken",
    "secret",
    "secretkey",
    "signingkey",
    "token",
    "webhooksecret",
}


def find_populated_sensitive_fields(value: object, path: tuple[str, ...] = ()) -> list[str]:
    fields: list[str] = []
    if isinstance(value, dict):
        for key, child in value.items():
            key_text = str(key)
            normalized_key = re.sub(r"[^a-z0-9]", "", key_text.lower())
            parent_names = {re.sub(r"[^a-z0-9]", "", part.lower()) for part in path}
            sensitive_name = (
                normalized_key in SENSITIVE_FIELD_NAMES
                or normalized_key.startswith("connectionstring")
                or (normalized_key == "key" and bool(parent_names & {"jwt", "encryption"}))
            )
            child_path = (*path, key_text)
            if sensitive_name and isinstance(child, str) and child.strip():
                fields.append(":".join(child_path))
            fields.extend(find_populated_sensitive_fields(child, child_path))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            fields.extend(find_populated_sensitive_fields(child, (*path, f"[{index}]")))
    return fields


def sensitive_field_detector_is_valid() -> bool:
    synthetic_config = {
        "Jwt": {"Key": "synthetic-test-value"},
        "Encryption": {"LegacyKey": "synthetic-test-value"},
        "ConnectionStrings": {"DefaultConnection": "Host=synthetic"},
        "Logging": {"LogLevel": {"Default": "Information"}},
    }
    actual = set(find_populated_sensitive_fields(synthetic_config))
    expected = {
        "Jwt:Key",
        "Encryption:LegacyKey",
        "ConnectionStrings:DefaultConnection",
    }
    return actual == expected


def main() -> int:
    if not sensitive_field_detector_is_valid():
        print("O detector de campos sensíveis não passou nos fixtures sintéticos.")
        return 1

    dockerignore = (ROOT / CONTEXT / ".dockerignore").read_text(encoding="utf-8")
    patterns = {line.strip() for line in dockerignore.splitlines() if line.strip() and not line.lstrip().startswith("#")}
    missing = sorted(REQUIRED_DOCKERIGNORE_PATTERNS - patterns)
    if missing:
        print(".dockerignore não exclui todos os bancos SQLite locais exigidos.")
        return 1

    gitignore = (ROOT / ".gitignore").read_text(encoding="utf-8")
    gitignore_patterns = {line.strip() for line in gitignore.splitlines() if line.strip() and not line.lstrip().startswith("#")}
    if not {
        "apps/backend/VendingMachines.Api/vending_machines.db",
        "apps/backend/VendingMachines.Api/vending_machines.db-*",
        "apps/backend/VendingMachines.Api/*.sqlite*",
    }.issubset(gitignore_patterns):
        print(".gitignore não protege o banco SQLite local e seus arquivos auxiliares.")
        return 1

    tracked = subprocess.run(
        ["git", "ls-files", "--", str(CONTEXT)],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.splitlines()
    databases = [
        path
        for path in tracked
        if Path(path).name.lower().endswith((".db", ".db-shm", ".db-wal", ".sqlite", ".sqlite-shm", ".sqlite-wal"))
    ]
    if databases:
        print("O contexto da API ainda rastreia um arquivo de banco local.")
        return 1

    config_path = ROOT / CONTEXT / "appsettings.json"
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        print("appsettings.json ausente ou inválido; não foi possível validar a configuração base.")
        return 1

    populated_sensitive_fields = find_populated_sensitive_fields(config)
    if populated_sensitive_fields:
        print(
            "appsettings.json contém valores preenchidos em campos sensíveis: "
            + ", ".join(sorted(populated_sensitive_fields))
        )
        return 1

    print("Contexto Docker exclui bancos locais; configuração base não contém valores sensíveis preenchidos.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
