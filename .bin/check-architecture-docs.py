#!/usr/bin/env python3
"""Fail when mapped code areas changed without their architecture page.

The code -> page map is the Markdown table in docs/architecture/README.md.
For every row whose code globs match a changed file, at least one mapped page
must also be changed, and every changed mapped page must contain a Mermaid
block and a "Last verified:" line. See docs/architecture/README.md.
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

MAP_FILE = Path("docs/architecture/README.md")
IGNORED_GLOBS = (
    "**/*.md",
    "**/*.lock",
    "**/package-lock.json",
    "**/pnpm-lock.yaml",
    "**/uv.lock",
    ".github/**",
    "tests/**",
    "**/*Tests/**",
    "**/*.spec.*",
    "**/*.test.*",
)
MERMAID_FENCE = re.compile(r"^```mermaid\s*$", re.MULTILINE)
LAST_VERIFIED = re.compile(r"^Last verified:\s*\d{4}-\d{2}-\d{2}\s+against\s+\S+", re.MULTILINE)
BACKTICKED = re.compile(r"`([^`]+)`")


def glob_to_regex(pattern):
    """Translate a repository glob where ** crosses directories and * does not."""
    out = []
    i = 0
    while i < len(pattern):
        c = pattern[i]
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif c == "*":
            out.append("[^/]*")
            i += 1
        elif c == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(c))
            i += 1
    return re.compile("^" + "".join(out) + "$")


def matches(path, patterns):
    return any(glob_to_regex(p).match(path) for p in patterns)


def parse_map(text):
    """Return [(code_globs, pages)] from the map table; skip header and separator rows."""
    rows = []
    for line in text.splitlines():
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 2 or set(cells[0]) <= {"-", ":", " "}:
            continue
        code = BACKTICKED.findall(cells[0])
        pages = BACKTICKED.findall(cells[1])
        if code and pages:
            rows.append((code, pages))
    return rows


def git(root, *args):
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return [line for line in result.stdout.splitlines() if line]


def changed_files(root, base, staged):
    if staged:
        return set(git(root, "diff", "--name-only", "--cached"))
    files = set()
    if base:
        try:
            files.update(git(root, "diff", "--name-only", f"{base}...HEAD"))
        except RuntimeError:
            files.update(git(root, "diff", "--name-only", base))
    files.update(git(root, "diff", "--name-only", "HEAD"))
    files.update(git(root, "ls-files", "--others", "--exclude-standard"))
    return files


def check_page(root, page):
    """Return a list of problems for one page (missing file, Mermaid or Last verified)."""
    path = root / page
    if not path.is_file():
        return [f"{page}: page does not exist"]
    text = path.read_text(encoding="utf-8")
    problems = []
    if not MERMAID_FENCE.search(text):
        problems.append(f"{page}: no ```mermaid block")
    if not LAST_VERIFIED.search(text):
        problems.append(f"{page}: missing 'Last verified: YYYY-MM-DD against <sha>' line")
    return problems


def evaluate(root, rows, changed):
    """Return (violations, checked_pages) for the changed files against the map."""
    violations = []
    checked = []
    code_changes = sorted(f for f in changed if not matches(f, IGNORED_GLOBS))
    for code_globs, pages in rows:
        hits = [f for f in code_changes if matches(f, code_globs)]
        if not hits:
            continue
        touched = [p for p in pages if p in changed]
        if not touched:
            violations.append(
                "Code changed without its architecture page:\n"
                + "".join(f"    {f}\n" for f in hits)
                + "  update one of: " + ", ".join(pages)
            )
            continue
        for page in touched:
            checked.append(page)
            violations.extend("  " + p for p in check_page(root, page))
    for _, pages in rows:
        for page in pages:
            if not (root / page).is_file():
                violations.append(f"  {page}: listed in the map but does not exist")
    return violations, checked


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="origin/main", help="git ref to diff against (default: origin/main)")
    parser.add_argument("--staged", action="store_true", help="only consider staged changes")
    parser.add_argument("--root", default=None, help="repository root (default: git toplevel)")
    parser.add_argument("--map", default=str(MAP_FILE), help="map file relative to the root")
    args = parser.parse_args(argv)

    try:
        root = Path(args.root) if args.root else Path(git(Path.cwd(), "rev-parse", "--show-toplevel")[0])
        map_path = root / args.map
        if not map_path.is_file():
            print(f"check-architecture-docs: map file not found: {map_path}", file=sys.stderr)
            return 2
        rows = parse_map(map_path.read_text(encoding="utf-8"))
        if not rows:
            print(f"check-architecture-docs: no map rows found in {map_path}", file=sys.stderr)
            return 2
        changed = changed_files(root, args.base, args.staged)
    except RuntimeError as error:
        print(f"check-architecture-docs: {error}", file=sys.stderr)
        return 2

    violations, checked = evaluate(root, rows, changed)
    if violations:
        print("check-architecture-docs: FAILED")
        for v in violations:
            print(v)
        print("Update the architecture page(s) with Mermaid diagrams, real type names, a 'Source:' line")
        print("and a 'Last verified:' line. See docs/architecture/README.md and docs/sdlc/document-architecture.md")
        print("(/document-architecture).")
        return 1
    scope = "staged changes" if args.staged else f"changes since {args.base}"
    print(f"check-architecture-docs: OK ({len(changed)} changed files, {len(checked)} architecture pages checked, {scope})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
