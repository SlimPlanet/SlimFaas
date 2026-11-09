#!/usr/bin/env python3
"""Check that campaign provenance copies only the fixed benchmark source."""
import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name("slimfaas-zlinq-benchmark.py").resolve()
SPEC = importlib.util.spec_from_file_location("zlinq_benchmark", SCRIPT)
BENCHMARK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BENCHMARK)


class ArchiveTests(unittest.TestCase):
    def test_initial_and_resumed_archives_match_the_hashed_fixed_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name in ("harness-as-run.py", "harness-resumed-123.py"):
                destination = Path(temporary) / name
                with patch.object(BENCHMARK, "__file__", "../../untrusted.py"):
                    BENCHMARK.archive_harness(destination)
                self.assertEqual(destination.read_bytes(), SCRIPT.read_bytes())
                self.assertEqual(hashlib.sha256(destination.read_bytes()).digest(),
                                 hashlib.sha256(BENCHMARK.HARNESS_SOURCE).digest())


if __name__ == "__main__":
    unittest.main()
