#!/usr/bin/env python3
"""Run the SDLC checkers before a pull request is created.

Used as a Claude Code PreToolUse hook (see .claude/settings.json): the hook
payload arrives on stdin as JSON; the checks only run when the Bash command
contains "gh pr create". Exit code 2 blocks the tool call and reports the
checker output; any other failure is informational. Run it by hand from the
repository root with no stdin to force the checks.
"""
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

BIN = Path(__file__).resolve().parent


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), BIN / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def hook_command():
    """Return the Bash command from the hook payload, or None when not called as a hook."""
    if sys.stdin is None or sys.stdin.isatty():
        return None
    raw = sys.stdin.read()
    if not raw.strip():
        return None
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        return None
    tool_input = payload.get("tool_input") or {}
    return tool_input.get("command") or ""


def main():
    command = hook_command()
    if command is not None and "gh pr create" not in command:
        return 0
    try:
        root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    except (OSError, subprocess.CalledProcessError):
        print("check-before-pr: not inside a git repository, skipping", file=sys.stderr)
        return 0
    failed = False
    print("check-before-pr: running architecture documentation check")
    if load("check-architecture-docs").main(["--root", str(root)]) != 0:
        failed = True
    print("check-before-pr: running rule mirror check")
    if load("check-agent-rules").main(str(root)) != 0:
        failed = True
    if failed:
        print("check-before-pr: fix the problems above before creating the pull request "
              "(docs/sdlc/pre-pr-checklist.md).", file=sys.stderr)
        return 2
    print("check-before-pr: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
