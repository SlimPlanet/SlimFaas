#!/usr/bin/env python3
"""Check that campaign provenance copies only the fixed benchmark source."""
import hashlib
from contextlib import redirect_stderr
import importlib.util
import io
import os
import subprocess
import sys
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name("slimfaas-zlinq-benchmark.py").resolve()
SPEC = importlib.util.spec_from_file_location("zlinq_benchmark", SCRIPT)
BENCHMARK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BENCHMARK)
SUMMARY_SPEC = importlib.util.spec_from_file_location("collection_summary", SCRIPT.with_name("summarize-collection-probe.py"))
SUMMARY = importlib.util.module_from_spec(SUMMARY_SPEC)
SUMMARY_SPEC.loader.exec_module(SUMMARY)
TRACE_SPEC = importlib.util.spec_from_file_location("offload_trace", SCRIPT.with_name("trace-async-offload.py"))
TRACE = importlib.util.module_from_spec(TRACE_SPEC)
TRACE_SPEC.loader.exec_module(TRACE)


class ArchiveTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows console control events")
    def test_hidden_console_shutdown_runs_the_owned_process_handler(self):
        command = [sys.executable, "-c", "import signal,sys\nsignal.signal(signal.SIGBREAK, lambda *args: sys.exit(0))\nprint('ready',flush=True)\nwhile True:\n    pass"]
        with BENCHMARK.start_local(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as process:
            self.assertEqual(process.stdout.readline().strip(), "ready")
            BENCHMARK.stop(process)
            self.assertEqual(process.returncode, 0)

    @unittest.skipUnless(os.name == "nt", "Windows process I/O API")
    def test_windows_io_counters_can_read_an_owned_process(self):
        with patch.object(BENCHMARK, "NODE_PIDS", [os.getpid()]):
            rows = BENCHMARK.io_counters()
        self.assertEqual(rows[0]["pid"], os.getpid())
        self.assertGreaterEqual(rows[0]["read_bytes"], 0)
        self.assertGreaterEqual(rows[0]["write_bytes"], 0)

    def test_trace_rejects_arbitrary_executable_paths_before_starting_a_process(self):
        with patch("sys.argv", ["trace", "--runtime", "untrusted.exe", "--output", "unused"]), \
             patch.object(TRACE.subprocess, "Popen") as process, redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRACE.main()
        process.assert_not_called()
        self.assertEqual(set(TRACE.RUNTIMES), {"baseline-jit", "candidate-jit", "baseline-aot", "candidate-aot",
                                             "candidate-fixed-jit", "candidate-fixed-aot"})

    def test_initial_and_resumed_archives_match_the_hashed_fixed_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name in ("harness-as-run.py", "harness-resumed-123.py"):
                destination = Path(temporary) / name
                with patch.object(BENCHMARK, "__file__", "../../untrusted.py"):
                    BENCHMARK.archive_harness(destination)
                self.assertEqual(destination.read_bytes(), SCRIPT.read_bytes())
                self.assertEqual(hashlib.sha256(destination.read_bytes()).digest(),
                                 hashlib.sha256(BENCHMARK.HARNESS_SOURCE).digest())

    def test_failed_file_cases_keep_the_failure_receipt(self):
        args = types.SimpleNamespace(file_bytes=32, verified_file_duration=1, data_concurrency=1)
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "files"
            metrics = {"cpu_seconds": 1, "rss_bytes": 100}
            with patch.object(BENCHMARK, "resources", return_value=metrics), \
                 patch.object(BENCHMARK.HTTP, "open", side_effect=OSError("injected transfer failure")):
                with self.assertRaisesRegex(RuntimeError, "file cases failed: reused, unique"):
                    BENCHMARK.verified_files_case(args, destination)
            report = BENCHMARK.json.loads((destination / "reused.json").read_text())
            self.assertEqual(report["summary"]["completed"], 0)
            self.assertEqual(len(report["summary"]["errors"]), 1)
            self.assertIn("injected transfer failure", report["summary"]["errors"][0]["error"])
            self.assertTrue((destination / "unique.json").exists())

    def test_resources_preserve_per_node_gc_counters(self):
        with patch.object(BENCHMARK, "get", return_value=("process_cpu_seconds_total 1\n"
                "process_working_set_bytes 2\ndotnet_collection_count_total{generation=\"0\"} 3\n")):
            result = BENCHMARK.resources()
        self.assertEqual(result["cpu_seconds"], 3)
        self.assertEqual(result["rss_bytes"], 6)
        self.assertEqual(result["runtime_counters_by_node"], [{"dotnet_collection_count_total{generation=\"0\"}": 3}] * 3)

    def test_collection_summary_rejects_duplicate_samples_and_behavior_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            rows = [{"Case": "control", "Sample": sample, "Result": 1, "Nanoseconds": 10, "Bytes": 0}
                    for sample in range(3)]
            for revision in ("baseline", "candidate"):
                for mode in ("jit", "aot"):
                    for repetition in (1, 2):
                        (root / f"round-{repetition}-{revision}-{mode}.json").write_text(BENCHMARK.json.dumps(rows))
            self.assertEqual(len(SUMMARY.summarize(root)["comparisons"]), 2)
            damaged = root / "round-2-candidate-aot.json"
            damaged.write_text(BENCHMARK.json.dumps(rows + rows[:1]))
            with self.assertRaisesRegex(ValueError, "Missing/duplicate"):
                SUMMARY.summarize(root)
            damaged.write_text(BENCHMARK.json.dumps([dict(row, Result=2) for row in rows]))
            with self.assertRaisesRegex(ValueError, "behavior differs"):
                SUMMARY.summarize(root)


if __name__ == "__main__":
    unittest.main()
