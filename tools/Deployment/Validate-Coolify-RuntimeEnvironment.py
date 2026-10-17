#!/usr/bin/env python3
"""Fail closed unless required production settings exist as runtime variables."""

import json
import re
import sys
from urllib.parse import urlsplit


def reject(message: str) -> None:
    print(f"::error::{message}", file=sys.stderr)
    raise SystemExit(1)


if len(sys.argv) != 2:
    reject("Uso: Validate-Coolify-RuntimeEnvironment.py <environment-json>")

try:
    with open(sys.argv[1], encoding="utf-8") as response_file:
        variables = json.load(response_file)
except (OSError, json.JSONDecodeError):
    reject("A lista de variáveis Coolify não é JSON válido.")

if not isinstance(variables, list):
    reject("A API do Coolify não retornou uma lista de variáveis da aplicação.")

runtime_values = {}
for variable in variables:
    if not isinstance(variable, dict):
        continue
    key = variable.get("key")
    if not isinstance(key, str) or variable.get("is_preview") is True:
        continue
    if variable.get("is_runtime") is not True:
        continue
    value = variable.get("real_value")
    if value is None:
        value = variable.get("value")
    if isinstance(value, str):
        runtime_values[key] = value


def enabled(key: str, default: bool) -> bool:
    value = runtime_values.get(key)
    if value is None or not value.strip():
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def is_placeholder(value: str) -> bool:
    normalized = value.lower()
    return any(marker in normalized for marker in (
        "change_me", "replace_with", "your_", "placeholder", "example", "dummy", "cole_aqui"
    ))


def is_masked(value: str) -> bool:
    normalized = value.strip().lower()
    return (bool(re.fullmatch(r"[*•#x]+", normalized, re.IGNORECASE)) and len(normalized) >= 3) or any(
        marker in normalized for marker in ("[redacted]", "<redacted>", "[hidden]", "<hidden>", "masked")
    )


required = [
    "ConnectionStrings__DefaultConnection",
    "Jwt__Key",
    "Encryption__Key",
    "MercadoPago__ClientSecret",
    "MercadoPago__WebhookSecret",
]
write_legacy = enabled("Encryption__WriteLegacyFormat", True)
require_legacy = enabled("Encryption__RequireLegacyData", True)
if write_legacy or require_legacy:
    required.append("Encryption__LegacyKey")

missing = [key for key in required if not runtime_values.get(key, "").strip()]
if missing:
    reject("Variáveis de runtime obrigatórias ausentes ou sem valor: " + ", ".join(missing))

invalid = [key for key in required if is_placeholder(runtime_values[key])]
if invalid:
    reject("Variáveis de runtime ainda contêm valores de exemplo: " + ", ".join(invalid))

masked = [key for key in required if is_masked(runtime_values[key])]
if masked:
    reject("A API do Coolify ocultou ou mascarou valores de runtime: " + ", ".join(masked))

jwt_key = runtime_values["Jwt__Key"]
if len(jwt_key.encode("utf-8")) < 32:
    reject("Jwt__Key precisa ter pelo menos 32 bytes.")

connection = runtime_values["ConnectionStrings__DefaultConnection"]
if re.search(r"sqlite|data\s+source|localhost|127\.0\.0\.1", connection, re.IGNORECASE):
    reject("ConnectionStrings__DefaultConnection precisa apontar para PostgreSQL fora do ambiente local.")


def connection_parameter(*names: str) -> str:
    alternatives = "|".join(re.escape(name) for name in names)
    match = re.search(rf"(?:^|;)\s*(?:{alternatives})\s*=\s*([^;]+)", connection, re.IGNORECASE)
    return match.group(1).strip().strip("{}\"'") if match else ""


if not connection_parameter("Host", "Server"):
    reject("ConnectionStrings__DefaultConnection precisa informar Host do PostgreSQL.")
if not connection_parameter("Database", "Initial Catalog"):
    reject("ConnectionStrings__DefaultConnection precisa informar Database do PostgreSQL.")
if not connection_parameter("Username", "User ID", "UserId"):
    reject("ConnectionStrings__DefaultConnection precisa informar Username do PostgreSQL.")
ssl_mode = connection_parameter("SSL Mode", "SslMode").lower()
if ssl_mode not in {"require", "verifyca", "verifyfull"}:
    reject("ConnectionStrings__DefaultConnection precisa exigir TLS com SSL Mode=Require, VerifyCA ou VerifyFull.")

if write_legacy and runtime_values["Encryption__Key"] != runtime_values["Encryption__LegacyKey"]:
    reject("Encryption__Key e Encryption__LegacyKey devem corresponder enquanto WriteLegacyFormat estiver habilitada.")
if not write_legacy and runtime_values.get("Encryption__LegacyKey") == runtime_values["Encryption__Key"]:
    reject("Encryption__Key deve ser diferente de Encryption__LegacyKey durante a rotação.")

if not enabled("AccountEmail__Enabled", False):
    reject("AccountEmail__Enabled precisa ser true: novos cadastros exigem confirmação de e-mail antes de uma release.")

if enabled("AccountEmail__Enabled", False):
    email_required = [
        "AccountEmail__Host",
        "AccountEmail__FromAddress",
        "AccountEmail__PublicBaseUrl",
    ]
    email_missing = [key for key in email_required if not runtime_values.get(key, "").strip()]
    if email_missing:
        reject("SMTP habilitado sem configuração obrigatória: " + ", ".join(email_missing))

    email_placeholders = [key for key in email_required if is_placeholder(runtime_values[key])]
    if email_placeholders:
        reject("SMTP habilitado com configuração de exemplo: " + ", ".join(email_placeholders))

    email_optional_secrets = ["AccountEmail__Username", "AccountEmail__Password"]
    for key in email_optional_secrets:
        value = runtime_values.get(key, "")
        if value and is_placeholder(value):
            reject("SMTP habilitado com segredo de exemplo: " + key)
    username = runtime_values.get("AccountEmail__Username", "").strip()
    password = runtime_values.get("AccountEmail__Password", "").strip()
    if bool(username) != bool(password):
        reject("SMTP exige Username e Password juntos, ou ambos vazios para relay sem autenticação.")

    sender = runtime_values["AccountEmail__FromAddress"].strip()
    if "@" not in sender or any(character.isspace() for character in sender):
        reject("AccountEmail__FromAddress não parece um endereço válido.")
    base_url = urlsplit(runtime_values["AccountEmail__PublicBaseUrl"].strip())
    if (base_url.scheme != "https" or not base_url.hostname or base_url.username or
            base_url.password or base_url.query or base_url.fragment):
        reject("AccountEmail__PublicBaseUrl precisa ser HTTPS sem credenciais, query ou fragmento.")

    port = runtime_values.get("AccountEmail__Port", "587").strip()
    timeout = runtime_values.get("AccountEmail__TimeoutSeconds", "15").strip()
    if not port.isdigit() or not 1 <= int(port) <= 65535:
        reject("AccountEmail__Port precisa estar entre 1 e 65535.")
    if not timeout.isdigit() or not 1 <= int(timeout) <= 120:
        reject("AccountEmail__TimeoutSeconds precisa estar entre 1 e 120.")

print("Variáveis de runtime obrigatórias presentes; valores não foram exibidos.")
