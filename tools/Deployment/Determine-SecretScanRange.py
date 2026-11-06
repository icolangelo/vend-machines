#!/usr/bin/env python3
"""Choose a fail-closed commit range for the incremental secret scan."""

from __future__ import annotations

import argparse
import subprocess
from pathlib import Path


def git(repo_root: Path, *args: str, git_command: str = "git") -> str:
    result = subprocess.run(
        [git_command, "-C", str(repo_root), *args],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def is_zero_sha(value: str) -> bool:
    return bool(value) and set(value) == {"0"}


def determine_range(
    repo_root: Path,
    event_name: str,
    head_sha: str,
    base_sha: str,
    before_sha: str,
    default_branch: str,
    git_command: str = "git",
) -> str:
    if event_name == "pull_request":
        if not base_sha or not head_sha:
            raise ValueError("SECRET_SCAN_BASE_SHA e SECRET_SCAN_HEAD_SHA são obrigatórios em pull requests.")
        return f"{base_sha}..{head_sha}"

    if before_sha and not is_zero_sha(before_sha):
        return f"{before_sha}..{head_sha}"

    if event_name == "push" and is_zero_sha(before_sha):
        default_ref = f"refs/remotes/origin/{default_branch}" if default_branch else ""
        if default_ref:
            try:
                git(repo_root, "rev-parse", "--verify", default_ref, git_command=git_command)
                merge_base = git(repo_root, "merge-base", head_sha, default_ref, git_command=git_command)
                return f"{merge_base}..{head_sha}"
            except subprocess.CalledProcessError:
                # An unrelated branch or a repository's first push has no common base.
                # Scan the full history reachable from its head instead of only the tip.
                return head_sha
        return head_sha

    try:
        parent_sha = git(repo_root, "rev-parse", f"{head_sha}^", git_command=git_command)
    except subprocess.CalledProcessError:
        # A root commit has no parent; scanning the head scans that commit itself.
        return head_sha
    return f"{parent_sha}..{head_sha}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, required=True)
    parser.add_argument("--event", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--base", default="")
    parser.add_argument("--before", default="")
    parser.add_argument("--default-branch", default="")
    parser.add_argument("--git-command", default="git")
    args = parser.parse_args()

    try:
        print(determine_range(
            args.repo_root,
            args.event,
            args.head,
            args.base,
            args.before,
            args.default_branch,
            args.git_command,
        ))
    except (ValueError, subprocess.CalledProcessError) as exc:
        print(f"::error::{exc}")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
