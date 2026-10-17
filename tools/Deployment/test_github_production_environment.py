#!/usr/bin/env python3
"""Offline tests for the fail-closed production environment validator."""

from __future__ import annotations

import unittest

from validate_github_production_environment import (
    REQUIRED_MAIN_CHECKS,
    ProtectionError,
    github_api_error_message,
    validate_main_branch_protection,
    validate_production_environment,
)


def environment_fixture() -> tuple[dict, dict]:
    return (
        {
            "name": "production",
            "protection_rules": [
                {
                    "type": "required_reviewers",
                    "prevent_self_review": True,
                    "reviewers": [{"type": "User", "reviewer": {"id": 123}}],
                }
            ],
            "deployment_branch_policy": {
                "protected_branches": False,
                "custom_branch_policies": True,
            },
        },
        {"branch_policies": [{"name": "main"}]},
    )


def branch_protection_fixture() -> dict:
    return {
        "required_pull_request_reviews": {
            "required_approving_review_count": 1,
            "dismiss_stale_reviews": True,
        },
        "enforce_admins": {"enabled": True},
        "required_status_checks": {
            "strict": True,
            "contexts": sorted(REQUIRED_MAIN_CHECKS),
        },
        "allow_force_pushes": {"enabled": False},
        "allow_deletions": {"enabled": False},
    }


class ProductionEnvironmentProtectionTests(unittest.TestCase):
    def test_reports_missing_main_protection(self) -> None:
        message = github_api_error_message(404, "https://api.github.com/repos/org/repo/branches/main/protection")
        self.assertIn("main não possui proteção", message)

    def test_reports_missing_production_environment(self) -> None:
        message = github_api_error_message(404, "https://api.github.com/repos/org/repo/environments/production")
        self.assertIn("ambiente GitHub production não existe", message)

    def test_reports_missing_api_permissions(self) -> None:
        message = github_api_error_message(403, "https://api.github.com/repos/org/repo/branches/main/protection")
        self.assertIn("Administration/Actions", message)

    def test_accepts_main_protection_with_reviewers_and_all_required_checks(self) -> None:
        validate_main_branch_protection(branch_protection_fixture())

    def test_rejects_unprotected_main(self) -> None:
        with self.assertRaisesRegex(ProtectionError, "aprovação de PR"):
            validate_main_branch_protection({})

    def test_rejects_missing_required_check(self) -> None:
        protection = branch_protection_fixture()
        protection["required_status_checks"]["contexts"].remove("Verify application / Backend tests")
        with self.assertRaisesRegex(ProtectionError, "Faltam checks"):
            validate_main_branch_protection(protection)

    def test_rejects_admin_bypass(self) -> None:
        protection = branch_protection_fixture()
        protection["enforce_admins"]["enabled"] = False
        with self.assertRaisesRegex(ProtectionError, "administradores"):
            validate_main_branch_protection(protection)

    def test_rejects_force_push(self) -> None:
        protection = branch_protection_fixture()
        protection["allow_force_pushes"]["enabled"] = True
        with self.assertRaisesRegex(ProtectionError, "force push"):
            validate_main_branch_protection(protection)

    def test_rejects_branch_deletion(self) -> None:
        protection = branch_protection_fixture()
        protection["allow_deletions"]["enabled"] = True
        with self.assertRaisesRegex(ProtectionError, "exclusão"):
            validate_main_branch_protection(protection)

    def test_rejects_stale_approval(self) -> None:
        protection = branch_protection_fixture()
        protection["required_pull_request_reviews"]["dismiss_stale_reviews"] = False
        with self.assertRaisesRegex(ProtectionError, "aprovações antigas"):
            validate_main_branch_protection(protection)

    def test_accepts_required_reviewer_self_review_block_and_main_only(self) -> None:
        environment, policies = environment_fixture()
        validate_production_environment(environment, policies)

    def test_rejects_missing_environment(self) -> None:
        environment, policies = environment_fixture()
        environment["name"] = "github-pages"
        with self.assertRaisesRegex(ProtectionError, "não existe"):
            validate_production_environment(environment, policies)

    def test_rejects_missing_reviewer(self) -> None:
        environment, policies = environment_fixture()
        environment["protection_rules"] = []
        with self.assertRaisesRegex(ProtectionError, "revisor obrigatório"):
            validate_production_environment(environment, policies)

    def test_rejects_self_approval(self) -> None:
        environment, policies = environment_fixture()
        environment["protection_rules"][0]["prevent_self_review"] = False
        with self.assertRaisesRegex(ProtectionError, "Prevent self-review"):
            validate_production_environment(environment, policies)

    def test_rejects_unrestricted_branches(self) -> None:
        environment, policies = environment_fixture()
        environment["deployment_branch_policy"] = None
        with self.assertRaisesRegex(ProtectionError, "padrões de branch"):
            validate_production_environment(environment, policies)

    def test_rejects_any_branch_pattern_other_than_main(self) -> None:
        environment, policies = environment_fixture()
        policies["branch_policies"].append({"name": "develop"})
        with self.assertRaisesRegex(ProtectionError, "somente a branch main"):
            validate_production_environment(environment, policies)


if __name__ == "__main__":
    unittest.main()
