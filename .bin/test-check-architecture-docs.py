#!/usr/bin/env python3
"""Test the architecture documentation checker against a throwaway git repository."""
import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("check_architecture_docs", Path(__file__).with_name("check-architecture-docs.py"))
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)

MAP = """# Architecture

| Code paths | Architecture pages |
|---|---|
| `src/Runtime/**`, `src/Shared/*.csproj` | `docs/runtime.md` |
| `src/Store/**` | `docs/store.md`, `docs/store-batching.md` |
"""
GOOD_PAGE = "# Runtime\n\n```mermaid\nflowchart LR\n  A[Worker] --> B[(Store)]\n```\n\nSource: src/Runtime/\n\nLast verified: 2026-09-11 against abc1234\n"
NO_MERMAID_PAGE = "# Store\n\nProse only.\n\nLast verified: 2026-09-11 against abc1234\n"


class GlobTests(unittest.TestCase):
    def test_double_star_crosses_directories_and_single_star_does_not(self):
        self.assertTrue(checker.matches("src/Runtime/Workers/SlimWorker.cs", ["src/Runtime/**"]))
        self.assertTrue(checker.matches("src/Shared/Shared.csproj", ["src/Shared/*.csproj"]))
        self.assertFalse(checker.matches("src/Shared/Nested/Shared.csproj", ["src/Shared/*.csproj"]))
        self.assertFalse(checker.matches("src/RuntimeTests/A.cs", ["src/Runtime/**"]))

    def test_ignored_globs_cover_tests_markdown_lockfiles_and_github(self):
        for path in ["tests/Runtime.Tests/A.cs", "src/App/App.Tests/B.cs", "docs/x.md", "pnpm-lock.yaml",
                     "src/ui/package-lock.json", ".github/workflows/main.yml", "src/ui/a.spec.tsx", "src/ui/a.test.ts"]:
            self.assertTrue(checker.matches(path, checker.IGNORED_GLOBS), path)
        self.assertFalse(checker.matches("src/Runtime/Program.cs", checker.IGNORED_GLOBS))


class MapTests(unittest.TestCase):
    def test_parse_map_reads_globs_and_pages_and_skips_header(self):
        rows = checker.parse_map(MAP)
        self.assertEqual(rows, [
            (["src/Runtime/**", "src/Shared/*.csproj"], ["docs/runtime.md"]),
            (["src/Store/**"], ["docs/store.md", "docs/store-batching.md"]),
        ])


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "user.name", "Test")
        self.write("docs/architecture/README.md", MAP)
        self.write("docs/runtime.md", GOOD_PAGE)
        self.write("docs/store.md", NO_MERMAID_PAGE)
        self.write("docs/store-batching.md", GOOD_PAGE)
        self.write("src/Runtime/Program.cs", "class A {}\n")
        self.write("src/Store/Store.cs", "class S {}\n")
        self.write("tests/Runtime.Tests/ATests.cs", "class T {}\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "baseline")
        self.git("checkout", "-q", "-b", "feature")

    def git(self, *args):
        subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def run_checker(self, *extra):
        return checker.main(["--root", str(self.root), "--base", "main", *extra])

    def test_untouched_repository_passes(self):
        self.assertEqual(self.run_checker(), 0)

    def test_code_change_without_page_fails(self):
        self.write("src/Runtime/Program.cs", "class A { int x; }\n")
        self.assertEqual(self.run_checker(), 1)

    def test_code_change_with_updated_page_passes_in_working_tree_and_after_commit(self):
        self.write("src/Runtime/Program.cs", "class A { int x; }\n")
        self.write("docs/runtime.md", GOOD_PAGE.replace("abc1234", "def5678"))
        self.assertEqual(self.run_checker(), 0)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "change runtime")
        self.assertEqual(self.run_checker(), 0)

    def test_page_without_mermaid_fails_even_when_touched(self):
        self.write("src/Store/Store.cs", "class S { int y; }\n")
        self.write("docs/store.md", NO_MERMAID_PAGE + "\nmore prose\n")
        self.assertEqual(self.run_checker(), 1)

    def test_any_one_of_several_pages_is_enough(self):
        self.write("src/Store/Store.cs", "class S { int y; }\n")
        self.write("docs/store-batching.md", GOOD_PAGE.replace("abc1234", "fed9876"))
        self.assertEqual(self.run_checker(), 0)

    def test_missing_last_verified_line_fails(self):
        self.write("src/Runtime/Program.cs", "class A { int x; }\n")
        self.write("docs/runtime.md", GOOD_PAGE.replace("Last verified: 2026-09-11 against abc1234\n", ""))
        self.assertEqual(self.run_checker(), 1)

    def test_test_only_and_markdown_only_changes_are_ignored(self):
        self.write("tests/Runtime.Tests/ATests.cs", "class T { }\n")
        self.write("README.md", "# hello\n")
        self.assertEqual(self.run_checker(), 0)

    def test_new_untracked_code_file_is_detected(self):
        self.write("src/Runtime/NewWorker.cs", "class W {}\n")
        self.assertEqual(self.run_checker(), 1)

    def test_staged_mode_only_looks_at_the_index(self):
        self.write("src/Runtime/Program.cs", "class A { int x; }\n")
        self.assertEqual(self.run_checker("--staged"), 0)
        self.git("add", "src/Runtime/Program.cs")
        self.assertEqual(self.run_checker("--staged"), 1)

    def test_page_listed_in_map_but_missing_fails(self):
        self.write("docs/architecture/README.md", MAP + "| `src/Other/**` | `docs/missing.md` |\n")
        self.assertEqual(self.run_checker(), 1)


if __name__ == "__main__":
    unittest.main()
