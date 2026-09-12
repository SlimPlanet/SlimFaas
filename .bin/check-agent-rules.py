#!/usr/bin/env python3
"""Fail when the Claude Code rules and the GitHub Copilot instructions drift apart.

Every .claude/rules/<topic>.md must have a twin .github/instructions/<topic>.instructions.md
(and vice versa) whose body, after the YAML front matter and without the
"<!-- Mirror of ... -->" comment line, is identical. See docs/sdlc/README.md.
"""
import difflib
import re
import sys
from pathlib import Path

CLAUDE_RULES = Path(".claude/rules")
COPILOT_INSTRUCTIONS = Path(".github/instructions")
MIRROR_COMMENT = re.compile(r"^<!--\s*Mirror of .*-->\s*$")


def body(path):
    lines = path.read_text(encoding="utf-8").splitlines()
    if lines and lines[0].strip() == "---":
        try:
            end = lines.index("---", 1)
        except ValueError:
            return None
        lines = lines[end + 1:]
    lines = [line.rstrip() for line in lines if not MIRROR_COMMENT.match(line)]
    while lines and not lines[0]:
        lines.pop(0)
    while lines and not lines[-1]:
        lines.pop()
    return lines


def main(root=None):
    root = Path(root) if root else Path.cwd()
    rules = {p.stem: p for p in (root / CLAUDE_RULES).glob("*.md")}
    instructions = {p.name[: -len(".instructions.md")]: p
                    for p in (root / COPILOT_INSTRUCTIONS).glob("*.instructions.md")}
    problems = []
    for topic in sorted(set(rules) | set(instructions)):
        if topic not in rules:
            problems.append(f"{instructions[topic]}: no twin {CLAUDE_RULES}/{topic}.md")
            continue
        if topic not in instructions:
            problems.append(f"{rules[topic]}: no twin {COPILOT_INSTRUCTIONS}/{topic}.instructions.md")
            continue
        left, right = body(rules[topic]), body(instructions[topic])
        if left is None or right is None:
            problems.append(f"{topic}: unterminated front matter")
            continue
        if left != right:
            diff = difflib.unified_diff(left, right, str(rules[topic]), str(instructions[topic]), lineterm="", n=1)
            problems.append(f"{topic}: bodies differ\n" + "\n".join(diff))
    if not rules and not instructions:
        problems.append(f"no rule files found under {root}")
    if problems:
        print("check-agent-rules: FAILED")
        for problem in problems:
            print(problem)
        print("Edit the rule body in both files at once; only the front matter and the mirror comment may differ.")
        return 1
    print(f"check-agent-rules: OK ({len(rules)} topics mirrored)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else None))
