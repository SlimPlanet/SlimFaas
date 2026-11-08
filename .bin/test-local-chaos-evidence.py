#!/usr/bin/env python3
"""Protect source files and paths outside the checkout when exporting evidence."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("evidence", Path(__file__).with_name("collect-local-chaos-evidence.py"))
evidence = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(evidence)


class ArtifactOutputTests(unittest.TestCase):
    def test_accepts_a_nested_artifact_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "artifacts"
            target = root / "chaos" / "evidence.json"
            self.assertEqual(target.resolve(), evidence.artifact_output(target, root))

    def test_rejects_parent_traversal_and_a_sibling_with_the_same_prefix(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "artifacts"
            for target in (root / ".." / "source.py", Path(directory) / "artifacts-other" / "evidence.json"):
                with self.subTest(target=target), self.assertRaisesRegex(ValueError, "inside"):
                    evidence.artifact_output(target, root)

    def test_rejects_an_existing_symbolic_link_to_an_external_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "artifacts"
            root.mkdir()
            outside = Path(directory) / "outside"
            outside.mkdir()
            link = root / "linked"
            try:
                link.symlink_to(outside, target_is_directory=True)
            except OSError as error:
                self.skipTest(f"Host cannot create directory symlinks: {error}")
            with self.assertRaisesRegex(ValueError, "inside"):
                evidence.artifact_output(link / "evidence.json", root)

    def test_preserves_existing_reports_and_input_campaigns(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            existing = root / "report.json"
            existing.write_text("original evidence", encoding="utf-8")
            for target, sources in ((existing, ()), (root / "campaign" / "report.json", (root / "campaign",)),
                                    (root / "source.py", ())):
                with self.subTest(target=target), self.assertRaises(ValueError):
                    evidence.artifact_output(target, root, sources)
            self.assertEqual("original evidence", existing.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
