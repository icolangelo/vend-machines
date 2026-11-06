#!/usr/bin/env python3
"""Require a reviewed impact and recovery note for risky EF migrations."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any


RISKY_OPERATIONS = {
    "AlterColumn",
    "AddColumn",
    "AddForeignKey",
    "AddPrimaryKey",
    "AddUniqueConstraint",
    "CreateCheckConstraint",
    "CreateTableForeignKey",
    "DeleteData",
    "ExecuteSqlInterpolated",
    "ExecuteSqlRaw",
    "InsertData",
    "RenameColumn",
    "RenameTable",
    "Sql",
    "UpdateData",
}
EXISTING_TABLE_OPERATIONS = {
    "AddColumn",
    "AddCheckConstraint",
    "AddForeignKey",
    "AddPrimaryKey",
    "AddUniqueConstraint",
    "CreateCheckConstraint",
}
SAFE_OR_CLASSIFIED_OPERATIONS = RISKY_OPERATIONS | EXISTING_TABLE_OPERATIONS | {
    "CreateIndex",
    "CreateTable",
}
TABLE_SCOPED_OPERATIONS = EXISTING_TABLE_OPERATIONS | {
    "CreateIndex",
    "DropForeignKey",
    "DropIndex",
}
UP_METHOD = re.compile(r"protected\s+override\s+void\s+Up\s*\([^)]*\)\s*\{", re.MULTILINE)
DOWN_METHOD = re.compile(r"protected\s+override\s+void\s+Down\s*\(", re.MULTILINE)
MIGRATION_ID = re.compile(r"^\d{14}_[A-Za-z0-9_]+$")


class MigrationReviewError(ValueError):
    pass


def normalized_source(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")


def source_digest(source: str) -> str:
    return hashlib.sha256(source.encode("utf-8")).hexdigest()


def _call_arguments(source: str, open_paren: int) -> str:
    """Return arguments for a C# call, matching parentheses outside strings."""
    depth = 0
    in_string = False
    escaped = False
    for index in range(open_paren, len(source)):
        char = source[index]
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return source[open_paren + 1:index]
    raise MigrationReviewError("Chamada C# com parênteses não balanceados.")


def is_risky_operation(operation: str) -> bool:
    return operation in RISKY_OPERATIONS or operation.startswith("Drop")


def migration_risks(path: Path, existing_tables: set[str] | None = None) -> tuple[set[str], set[str], set[str], set[str], str]:
    migration_id = path.stem
    if not MIGRATION_ID.fullmatch(migration_id):
        raise MigrationReviewError(f"Nome de migration inválido: {path.name}")

    source = normalized_source(path)
    up_match = UP_METHOD.search(source)
    if not up_match:
        raise MigrationReviewError(f"Método Up não encontrado em {path.name}.")

    down_match = DOWN_METHOD.search(source, up_match.end())
    up_source = source[up_match.end():down_match.start() if down_match else len(source)]
    calls = set(re.findall(r"\bmigrationBuilder\s*\.\s*(\w+)\s*(?:<[^;()]*>)?\s*\(", up_source))
    unclassified_calls = sorted(
        operation for operation in calls
        if operation not in SAFE_OR_CLASSIFIED_OPERATIONS and not operation.startswith("Drop")
    )
    if unclassified_calls:
        raise MigrationReviewError(
            f"Operação(ões) migrationBuilder sem classificação em {path.name}: "
            f"{', '.join(unclassified_calls)}. Classifique como segura ou arriscada antes de liberar."
        )
    created_tables = set(re.findall(
        r'\bmigrationBuilder\s*\.\s*CreateTable\s*\(\s*name\s*:\s*"([^"]+)"',
        up_source,
    ))
    for operation in TABLE_SCOPED_OPERATIONS & calls:
        for call in re.finditer(
            rf"\bmigrationBuilder\s*\.\s*{re.escape(operation)}\s*(?:<[^;()]*>)?\s*\(([\s\S]*?)\);",
            up_source,
        ):
            table_match = re.search(r'\btable\s*:\s*"([^"]+)"', call.group(1))
            if table_match:
                table = table_match.group(1)
                if table not in (existing_tables or set()) and table not in created_tables:
                    raise MigrationReviewError(
                        f"{migration_id}: {operation} referencia a tabela {table!r} sem origem "
                        "conhecida no histórico de migrations; confirme o schema ou registre a tabela."
                    )
    created_table_foreign_keys: set[str] = set()
    for create_table in re.finditer(r"\bmigrationBuilder\s*\.\s*CreateTable\s*\(", up_source):
        open_paren = up_source.find("(", create_table.start())
        arguments = _call_arguments(up_source, open_paren)
        for foreign_key in re.finditer(r"\btable\s*\.\s*ForeignKey\s*\(", arguments):
            foreign_key_open_paren = arguments.find("(", foreign_key.start())
            foreign_key_arguments = _call_arguments(arguments, foreign_key_open_paren)
            principal = re.search(r'\bprincipalTable\s*:\s*"([^"]+)"', foreign_key_arguments)
            if principal:
                principal_table = principal.group(1)
                if principal_table not in (existing_tables or set()) and principal_table not in created_tables:
                    raise MigrationReviewError(
                        f"{migration_id}: chave estrangeira referencia a tabela {principal_table!r} "
                        "sem origem conhecida no histórico de migrations."
                    )
                if principal_table in (existing_tables or set()):
                    created_table_foreign_keys.add(principal_table)
    affected_existing_tables: set[str] = set()
    populated_table_indexes: set[str] = set()
    for call in re.finditer(r"\bmigrationBuilder\s*\.\s*CreateIndex\s*\(([\s\S]*?)\);", up_source):
        table_match = re.search(r'table\s*:\s*"([^"]+)"', call.group(1))
        if table_match:
            table = table_match.group(1)
            if table in (existing_tables or set()) and table not in created_tables:
                populated_table_indexes.add(table)

    for call in re.finditer(r'\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?"[^"\r\n]+"\s+ON\s+"([^"\r\n]+)"', up_source, re.IGNORECASE):
        table = call.group(1)
        if table in (existing_tables or set()) and table not in created_tables:
            populated_table_indexes.add(table)
            affected_existing_tables.add(table)

    risks = {operation for operation in calls if is_risky_operation(operation)}
    if created_table_foreign_keys:
        risks.add("CreateTableForeignKey")
    affected_existing_tables.update(created_table_foreign_keys)
    risky_existing_table_operations: set[str] = set()
    for operation in EXISTING_TABLE_OPERATIONS & calls:
        for call in re.finditer(
            rf"\bmigrationBuilder\s*\.\s*{re.escape(operation)}\s*(?:<[^;()]*>)?\s*\(([\s\S]*?)\);",
            up_source,
        ):
            table_match = re.search(r'\btable\s*:\s*"([^"]+)"', call.group(1))
            if table_match:
                table = table_match.group(1)
                if table in (existing_tables or set()) and table not in created_tables:
                    affected_existing_tables.add(table)
                    risky_existing_table_operations.add(operation)
                if operation == "AddForeignKey":
                    principal = re.search(r'\bprincipalTable\s*:\s*"([^"]+)"', call.group(1))
                    if principal:
                        principal_table = principal.group(1)
                        if principal_table not in (existing_tables or set()) and principal_table not in created_tables:
                            raise MigrationReviewError(
                                f"{migration_id}: chave estrangeira referencia a tabela {principal_table!r} "
                                "sem origem conhecida no histórico de migrations."
                            )
                        if principal_table in (existing_tables or set()):
                            affected_existing_tables.add(principal_table)
    risks.difference_update(EXISTING_TABLE_OPERATIONS)
    risks.update(risky_existing_table_operations)

    for call in re.finditer(
        r"\bmigrationBuilder\s*\.\s*(\w+)\s*(?:<[^;()]*>)?\s*\(([\s\S]*?)\);",
        up_source,
    ):
        operation, arguments = call.groups()
        if operation in {"CreateTable", "RenameTable", "DropTable"}:
            table_match = re.search(r'\bname\s*:\s*"([^"]+)"', arguments)
        else:
            table_match = re.search(r'\btable\s*:\s*"([^"]+)"', arguments)
        if table_match:
            table = table_match.group(1)
            if table in (existing_tables or set()) and table not in created_tables:
                affected_existing_tables.add(table)

    if "Sql" in calls:
        sql_table_references = re.compile(
            r'\b(?:FROM|JOIN|UPDATE|INTO|TABLE|REFERENCES|ON)\s+(?:\\"([^\\"]+)\\"|"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))',
            re.IGNORECASE,
        )
        for match in sql_table_references.finditer(up_source):
            table = match.group(1) or match.group(2) or match.group(3)
            if table in (existing_tables or set()) and table not in created_tables:
                affected_existing_tables.add(table)
    if populated_table_indexes:
        risks.add("CreateIndex")
    return risks, populated_table_indexes, created_tables, affected_existing_tables, source


def validate_reviews(migrations_dir: Path, manifest_path: Path) -> list[tuple[str, list[str]]]:
    try:
        manifest: Any = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise MigrationReviewError(f"Não foi possível ler o registro de risco {manifest_path}: {exc}") from exc
    if not isinstance(manifest, dict):
        raise MigrationReviewError("O registro de risco precisa ser um objeto JSON indexado pelo ID da migration.")

    detected: dict[str, tuple[set[str], set[str], set[str], str]] = {}
    known_tables: set[str] = set()
    for path in sorted(migrations_dir.glob("*.cs")):
        if path.name.endswith(".Designer.cs") or not MIGRATION_ID.fullmatch(path.stem):
            continue
        risks, indexed_tables, created_tables, affected_tables, source = migration_risks(path, known_tables)
        if risks:
            detected[path.stem] = (risks, indexed_tables, affected_tables, source)
        known_tables.update(created_tables)

    detected_ids = set(detected)
    reviewed_ids = set(manifest)
    errors: list[str] = []
    for migration_id in sorted(detected_ids - reviewed_ids):
        errors.append(f"{migration_id}: operação de risco sem revisão registrada ({', '.join(sorted(detected[migration_id][0]))}).")
    for migration_id in sorted(reviewed_ids - detected_ids):
        errors.append(f"{migration_id}: registro obsoleto ou migration sem operação de risco.")

    for migration_id in sorted(detected_ids & reviewed_ids):
        risks, indexed_tables, affected_tables, source = detected[migration_id]
        entry = manifest[migration_id]
        if not isinstance(entry, dict):
            errors.append(f"{migration_id}: a revisão precisa conter operações, impacto, recuperação e hash.")
            continue
        expected_operations = sorted(risks)
        actual_operations = entry.get("operations")
        if actual_operations != expected_operations:
            errors.append(f"{migration_id}: operações registradas divergem; esperado {expected_operations}.")
        expected_index_tables = sorted(indexed_tables)
        if entry.get("index_tables", []) != expected_index_tables:
            errors.append(f"{migration_id}: tabelas com novos índices divergem; esperado {expected_index_tables}.")
        expected_affected_tables = sorted(affected_tables)
        if entry.get("affected_tables") != expected_affected_tables:
            errors.append(f"{migration_id}: tabelas afetadas divergem; esperado {expected_affected_tables}.")
        for field in ("impact", "recovery"):
            value = entry.get(field)
            if not isinstance(value, str) or len(value.strip()) < 30:
                errors.append(f"{migration_id}: campo {field!r} precisa explicar a decisão em pelo menos 30 caracteres.")
        if entry.get("source_sha256") != source_digest(source):
            errors.append(f"{migration_id}: migration mudou desde a revisão; reavalie o impacto e atualize o hash.")

    if errors:
        raise MigrationReviewError("\n".join(errors))
    return [(migration_id, sorted(risks)) for migration_id, (risks, _, _, _) in sorted(detected.items())]


def main() -> int:
    repo_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--migrations", type=Path, default=repo_root / "apps/backend/VendingMachines.Api/Migrations")
    parser.add_argument("--manifest", type=Path, default=repo_root / "docs/migration-risk-review.json")
    args = parser.parse_args()

    try:
        reviewed = validate_reviews(args.migrations, args.manifest)
    except MigrationReviewError as exc:
        print(f"::error::Revisão de risco de migrations falhou:\n{exc}", file=sys.stderr)
        return 1

    print(f"Revisão de risco aprovada: {len(reviewed)} migrations com impacto/recovery, locks de índices e hash conferidos.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
