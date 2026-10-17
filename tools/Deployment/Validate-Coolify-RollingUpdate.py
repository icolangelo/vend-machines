#!/usr/bin/env python3
"""Fail closed unless the configured Coolify app can overlap old/new API containers."""

import json
import re
import sys
from urllib.parse import urlsplit


def reject(message: str) -> None:
    print(f"::error::{message}", file=sys.stderr)
    raise SystemExit(1)


if len(sys.argv) != 6:
    reject("Uso: Validate-Coolify-RollingUpdate.py <application-json> <application-uuid> <production-host> <image-name> <image-tag>")

try:
    with open(sys.argv[1], encoding="utf-8") as response_file:
        app = json.load(response_file)
except (OSError, json.JSONDecodeError):
    reject("A configuração da aplicação Coolify não é JSON válido.")

expected_uuid = sys.argv[2]
if not isinstance(app, dict) or app.get("uuid") != expected_uuid:
    reject("A API do Coolify não retornou a aplicação indicada pelo webhook.")

expected_image_name = sys.argv[4].strip().lower().rstrip("/")
expected_image_tag = sys.argv[5].strip()
if app.get("build_pack") != "dockerimage":
    reject("A aplicação Coolify deve usar Docker Image, para receber exatamente a imagem revisada pelo workflow.")
if str(app.get("docker_registry_image_name") or "").strip().lower().rstrip("/") != expected_image_name:
    reject("A imagem Docker Image do Coolify não corresponde ao repositório GHCR deste workflow.")
if str(app.get("docker_registry_image_tag") or "").strip() != expected_image_tag:
    reject("A tag Docker Image do Coolify não corresponde à tag promovida por este workflow.")

expected_host = sys.argv[3].lower().rstrip(".")
fqdn_values = app.get("fqdn") or app.get("domains")
if isinstance(fqdn_values, str):
    fqdn_candidates = re.split(r"[,;\n]+", fqdn_values)
elif isinstance(fqdn_values, list):
    fqdn_candidates = [value for value in fqdn_values if isinstance(value, str)]
else:
    fqdn_candidates = []

configured_hosts = set()
for candidate in fqdn_candidates:
    candidate = candidate.strip()
    if not candidate:
        continue
    try:
        parsed = urlsplit(candidate if "://" in candidate else f"//{candidate}")
    except ValueError:
        continue
    if parsed.username or parsed.password:
        continue
    if parsed.hostname:
        configured_hosts.add(parsed.hostname.lower().rstrip("."))

if expected_host not in configured_hosts:
    reject("O domínio da aplicação Coolify não corresponde ao host do health check de produção.")

if str(app.get("ports_mappings") or "").strip():
    reject("Rolling update bloqueado: ports_mappings publica porta no host e impede sobreposição dos containers.")

settings = app.get("settings")
if not isinstance(settings, dict):
    reject("A API do Coolify não retornou as configurações necessárias para validar rolling update.")
if settings.get("is_auto_deploy_enabled") is not False:
    reject("Deploy automático do Coolify precisa estar explicitamente desativado; a versão só pode mudar após aprovação do workflow manual.")
if settings.get("is_consistent_container_name_enabled") is not False:
    reject("Rolling update exige Consistent Container Names desativado.")
if str(settings.get("custom_internal_name") or "").strip():
    reject("Rolling update bloqueado por Custom Container Name configurado.")

run_options = str(app.get("custom_docker_run_options") or "")
if re.search(r"(?:^|\s)--ip(?:6)?(?:=|\s|$)", run_options):
    reject("Rolling update bloqueado por IP fixo em custom_docker_run_options.")

if app.get("health_check_enabled") is not True:
    reject("Configure health check ativo no Coolify antes da release.")
if app.get("health_check_path") != "/health/ready":
    reject("O health check Coolify deve consultar /health/ready.")
if str(app.get("health_check_port") or "") != "8080":
    reject("O health check Coolify deve usar a porta interna 8080.")
if str(app.get("health_check_method") or "").upper() != "GET":
    reject("O health check Coolify deve usar GET.")
if app.get("health_check_return_code") not in (None, 200):
    reject("O health check Coolify deve exigir HTTP 200 de /health/ready.")

grace_period = settings.get("stop_grace_period")
if not isinstance(grace_period, int) or isinstance(grace_period, bool) or grace_period < 60:
    reject("Configure Stop Grace Period de pelo menos 60 segundos no Coolify.")

print("Configuração Coolify elegível para rolling update e health check Ready validada.")
