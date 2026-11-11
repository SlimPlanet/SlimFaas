#!/usr/bin/env python3
"""Exercise receipt integrity, process fencing and configurable benchmark endpoints."""

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


WORKLOAD = load("platform_workload", ROOT / ".bin/run-platform-workload.py")
FIXTURE = load("platform_fixture", ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py")
AGENT = load("platform_agent", ROOT / ".bin/slimdata_fault_agent.py")
SINK = load("platform_sink", ROOT / ".bin/slimdata_receipt_sink.py")
MATRIX = load("platform_matrix", ROOT / ".bin/run-platform-matrix.py")


class PlatformTests(unittest.TestCase):
    def test_memory_comparison_distinguishes_regression_failure_and_missing_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = [root / name for name in ("baseline", "candidate")]
            for path in paths:
                path.mkdir()
                (path / "status.json").write_text('{"passed":true}')
                (path / "operations.receipts.json").write_text('{"passed":true}')
                (path / "operations.json").write_text(json.dumps({"validationFailures": 0, "report": {
                    "Failed": 0, "Rate": 100, "Operations": [{"Operation": "All", "P99Milliseconds": 10}]}}))
                (path / "memory.csv").write_text("phase,rss_kb\nload,102400\nvalidation,9999999\n")
            self.assertEqual("PASS", MATRIX.compare_memory(*paths)["verdict"])
            (paths[1] / "memory.csv").write_text("phase,rss_kb\nload,120000\n")
            self.assertEqual("FAIL", MATRIX.compare_memory(*paths)["verdict"])
            (paths[1] / "status.json").write_text('{"passed":false}')
            self.assertEqual("FAIL", MATRIX.compare_memory(*paths)["verdict"])
            (paths[0] / "status.json").write_text('{"passed":false}')
            self.assertEqual("INCONCLUSIVE", MATRIX.compare_memory(*paths)["verdict"])
            (paths[0] / "status.json").unlink()
            self.assertEqual("INCONCLUSIVE", MATRIX.compare_memory(*paths)["verdict"])

    def test_bracket_conflicts_are_detected_per_guardrail_even_when_both_runs_fail(self):
        pairs = {f"a{a}-b{b}": {"verdict": "FAIL", "checks": {"p99": False, "rss": True}}
                 for a in (1, 4) for b in (2, 3)}
        self.assertFalse(MATRIX.contradictory(pairs))
        pairs["a4-b2"]["checks"] = {"p99": True, "rss": False}
        self.assertTrue(MATRIX.contradictory(pairs))

    def test_receipts_keep_retry_chronology_across_pods_and_restarts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "receipts-a.jsonl").write_text(json.dumps({"utc": 3, "attempt": 2}) + "\n")
            (root / "receipts-z.jsonl").write_text(
                json.dumps({"utc": 1, "attempt": 0}) + "\n" +
                json.dumps({"utc": 2, "attempt": 1}) + "\n" + '{"utc": 4')
            self.assertEqual([0, 1, 2], [row["attempt"] for row in WORKLOAD.LOCAL.read_receipts(root)])

    def test_kubernetes_readiness_uses_the_real_apis_without_a_local_supervisor(self):
        local = WORKLOAD.LOCAL
        campaign = object.__new__(local.Campaign)
        campaign.process = None
        campaign.options = Mock(recovery_timeout=1)
        campaign.ports = [35121, 35122, 35123]
        campaign.entry = 35120
        campaign.client = Mock()
        campaign.phase = "startup"
        campaign.generation = 1
        def request(_port, path, **_kwargs):
            return (404 if path.startswith("/data/sets/") else 200), {}, b""
        with patch.object(local, "request", side_effect=request):
            campaign.ready()
        campaign.client.append.assert_called_once()

    def test_receipt_reconciliation_requires_actual_unique_delivery_and_checksum(self):
        for failure in ("missing", "duplicate", "checksum", "bytes", "status", None):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                document = {"report": {"KeyPrefix": "load", "PayloadBytes": 4}, "samples": [
                    {"operation": "Async", "Success": True, "Worker": 1, "Sequence": 2}]}
                output = root / "operations.json"
                output.write_text(json.dumps(document))
                receipt = {"event": "completed", "id": "load-async-1-2", "bytes": 4,
                           "sha256": hashlib.sha256(b"abcd").hexdigest(), "status": 200}
                if failure == "checksum":
                    receipt["sha256"] = "wrong"
                if failure == "bytes":
                    receipt["bytes"] = 3
                if failure == "status":
                    receipt["status"] = 500
                lines = [] if failure == "missing" else [receipt] * (2 if failure == "duplicate" else 1)
                (root / "receipts.jsonl").write_text("".join(json.dumps(row) + "\n" for row in lines))
                if failure:
                    with self.assertRaises(AssertionError):
                        WORKLOAD.verify_receipts(output, root)
                else:
                    WORKLOAD.verify_receipts(output, root)
                    self.assertTrue(json.loads(output.with_suffix(".receipts.json").read_text())["passed"])

    def test_same_pid_writers_never_share_a_receipt_file(self):
        with tempfile.TemporaryDirectory() as directory:
            first = FIXTURE.Journal(Path(directory))
            second = FIXTURE.Journal(Path(directory))
            self.assertNotEqual(first.path, second.path)
            first.append({"event": "job", "args": ["first"]})
            second.append({"event": "job", "args": ["second"]})
            self.assertEqual(["first"], json.loads(first.path.read_text())["args"])
            self.assertEqual(["second"], json.loads(second.path.read_text())["args"])

    def test_kubernetes_job_acknowledges_the_independent_receipt_sink(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            server = SINK.build_server(root / "sink", 0)
            running = threading.Thread(target=server.serve_forever)
            running.start()
            try:
                with patch.dict(os.environ, {"CHAOS_RECEIPT_URL": f"http://127.0.0.1:{server.server_port}/receipts"}):
                    journal = FIXTURE.Journal(root / "producer")
                    journal.append({"event": "job", "args": ["actual-job"]})
                sink = next((root / "sink").glob("*.jsonl"))
                self.assertEqual(["actual-job"], json.loads(sink.read_text())["args"])
                self.assertEqual(json.loads(journal.path.read_text()), json.loads(sink.read_text()))
            finally:
                server.shutdown()
                server.server_close()
                running.join()

    def test_failed_remote_receipt_acknowledgement_is_not_reported_as_job_success(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"CHAOS_RECEIPT_URL": "http://receipt.invalid/receipts"}):
            journal = FIXTURE.Journal(Path(directory))
            with patch.object(journal.http, "open", side_effect=OSError("collector unavailable")):
                with self.assertRaisesRegex(OSError, "collector unavailable"):
                    journal.append({"event": "job", "args": ["unknown"]})

    def test_stale_process_identity_cannot_signal_a_restarted_or_foreign_runtime(self):
        current = {"pid": 12, "started": "300", "pod_uid": "owned"}
        for identity in (current | {"started": "299"}, current | {"pod_uid": "foreign"}):
            request = object.__new__(AGENT.Handler)
            body = json.dumps({"action": "kill", "identity": identity}).encode()
            request.rfile = io.BytesIO(body)
            request.path = "/control"
            request.headers = {"Content-Length": str(len(body))}
            request.reply = Mock()
            with patch.object(AGENT, "runtime_identity", return_value=current), patch.object(AGENT.os, "kill") as kill:
                request.do_POST()
            kill.assert_not_called()
            self.assertEqual(409, request.reply.call_args.args[0])

    def test_memorylab_rejects_non_http_or_ambiguous_node_origins(self):
        runtime = ROOT / "tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"
        if not runtime.exists():
            self.skipTest("Build MemoryLab Release before running endpoint integration checks")
        for urls in ("file:///tmp", "http://user:password@localhost", "http://localhost/prefix", ","):
            result = subprocess.run(["dotnet", str(runtime), "load", "--node-urls", urls], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("node", result.stderr.lower())


if __name__ == "__main__":
    unittest.main()
