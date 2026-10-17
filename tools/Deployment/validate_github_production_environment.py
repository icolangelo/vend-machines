#!/usr/bin/env python3
"""Fail closed unless GitHub's production deployment environment is protected."""

from __future__ import annotations

import json
import os
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


class ProtectionError(ValueError):
    pass


REQUIRED_MAIN_CHECKS = {
    "Verify application / Backend tests",
    "Verify application / Frontend types, lint, tests and build",
    "Scan commit changes for secrets / Gitleaks changed commits",
}


def validate_main_branch_protection(protection: dict) -> None:
    reviews = protection.get("required_pull_request_reviews") or {}
    if reviews.get("required_approving_review_count", 0) < 1:
        raise ProtectionError("A branch main precisa exigir pelo menos uma aprovação de PR.")
    if reviews.get("dismiss_stale_reviews") is not True:
        raise ProtectionError("A branch main precisa invalidar aprovações antigas após novas alterações.")

    if (protection.get("enforce_admins") or {}).get("enabled") is not True:
        raise ProtectionError("A proteção de main também precisa valer para administradores.")

    status_checks = protection.get("required_status_checks") or {}
    if status_checks.get("strict") is not True:
        raise ProtectionError("A branch main precisa exigir atualização com a base antes do merge.")
    actual_checks = set(status_checks.get("contexts") or [])
    missing_checks = REQUIRED_MAIN_CHECKS - actual_checks
    if missing_checks:
        raise ProtectionError("Faltam checks obrigatórios em main: " + ", ".join(sorted(missing_checks)))

    if (protection.get("allow_force_pushes") or {}).get("enabled") is not False:
        raise ProtectionError("A branch main não pode permitir force push.")
    if (protection.get("allow_deletions") or {}).get("enabled") is not False:
        raise ProtectionError("A branch main não pode permitir exclusão.")


def validate_production_environment(environment: dict, branch_policies: dict) -> None:
    if environment.get("name") != "production":
        raise ProtectionError("O ambiente GitHub production não existe ou não pôde ser confirmado.")

    reviewers_rule = next(
        (
            rule
            for rule in environment.get("protection_rules", [])
            if rule.get("type") == "required_reviewers"
        ),
        None,
    )
    reviewers = reviewers_rule.get("reviewers", []) if reviewers_rule else []
    valid_reviewers = [
        item
        for item in reviewers
        if item.get("type") in {"User", "Team"}
        and isinstance(item.get("reviewer"), dict)
        and isinstance(item["reviewer"].get("id"), int)
        and item["reviewer"]["id"] > 0
    ]
    if not valid_reviewers:
        raise ProtectionError("Configure pelo menos um usuário ou time como revisor obrigatório de production.")
    if reviewers_rule.get("prevent_self_review") is not True:
        raise ProtectionError("Ative Prevent self-review no ambiente production.")

    branch_policy = environment.get("deployment_branch_policy") or {}
    if branch_policy.get("protected_branches") is not False or branch_policy.get("custom_branch_policies") is not True:
        raise ProtectionError("Restrinja o ambiente production por padrões de branch explícitos.")

    policies = branch_policies.get("branch_policies", [])
    names = [policy.get("name") for policy in policies]
    if names != ["main"]:
        raise ProtectionError("O ambiente production deve permitir somente a branch main.")


def get_github_json(url: str, token: str) -> dict:
    request = Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "User-Agent": "vend-machines-production-preflight",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urlopen(request, timeout=15) as response:
        return json.loads(response.read())


def github_api_error_message(status: int, url: str) -> str:
    if status == 404 and "/branches/main/protection" in url:
        return "A branch main não possui proteção clássica configurada."
    if status == 404 and "/environments/production/deployment-branch-policies" in url:
        return "Configure a política de branches do ambiente production para permitir somente main."
    if status == 404 and "/environments/production" in url:
        return "O ambiente GitHub production não existe."
    if status == 403 and "/branches/main/protection" in url:
        return "GITHUB_ADMIN_READ_TOKEN precisa ter a permissão Administration: read para consultar a proteção de main."
    if status == 403 and "/environments/" in url:
        return "GITHUB_TOKEN precisa ter a permissão Actions: read para consultar o ambiente production."
    return "A consulta autenticada da proteção do ambiente falhou."


def main() -> int:
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    token = os.environ.get("GITHUB_TOKEN", "")
    admin_read_token = os.environ.get("GITHUB_ADMIN_READ_TOKEN", "")
    if not admin_read_token:
        print("::error::Configure GITHUB_ADMIN_READ_TOKEN no ambiente production com permissão Administration: read.", file=sys.stderr)
        return 1
    if repository.count("/") != 1 or not token:
        print("::error::Não foi possível consultar a proteção do ambiente production.", file=sys.stderr)
        return 1

    encoded_environment = quote("production", safe="")
    repository_url = f"https://api.github.com/repos/{repository}"
    base_url = f"{repository_url}/environments/{encoded_environment}"
    try:
        branch_protection = get_github_json(repository_url + "/branches/main/protection", admin_read_token)
        validate_main_branch_protection(branch_protection)
        environment = get_github_json(base_url, token)
        policies = get_github_json(base_url + "/deployment-branch-policies", token)
        validate_production_environment(environment, policies)
    except (HTTPError, URLError, TimeoutError, OSError, ValueError, json.JSONDecodeError) as error:
        if isinstance(error, ProtectionError):
            message = str(error)
        elif isinstance(error, HTTPError):
            message = github_api_error_message(error.code, error.url)
        else:
            message = "A consulta autenticada da proteção do ambiente falhou."
        print(f"::error::{message}", file=sys.stderr)
        return 1

    print("Proteção do ambiente production confirmada: revisor obrigatório, sem autoaprovação e somente branch main.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
