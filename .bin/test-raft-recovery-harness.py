#!/usr/bin/env python3
"""Deterministic failure checks for the native Raft recovery harness."""

from concurrent.futures import Future
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError


SPEC = importlib.util.spec_from_file_location(
    "raft_recovery", Path(__file__).with_name("test-slimdata-raft-recovery.py"))
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class RecoveryHarnessTests(unittest.TestCase):
    def test_idle_outage_waits_for_diagnostic_recovery_and_both_log_records(self):
        with tempfile.TemporaryDirectory() as output:
            cluster = HARNESS.Cluster(Path(output))
            log_path = Path(output) / "node-0-0.log"
            log_path.write_text("")
            notification = "SlimData Raft consensus unavailable.\n"
            state = {"paused": False, "healthy_reads": 0, "outage_reads": 0}

            def signal(value):
                if value == HARNESS.signal.SIGSTOP:
                    self.assertGreaterEqual(state["healthy_reads"], 2,
                                            "Must observe diagnostics recovered before pausing")
                    state["paused"] = True
                elif value == HARNESS.signal.SIGCONT:
                    self.assertGreaterEqual(state["outage_reads"], 2,
                                            "Must wait for the reminder to reach the log")
                    state["paused"] = False

            cluster.processes = {0: Mock(), 1: Mock(), 2: Mock()}
            for process in cluster.processes.values():
                process.poll.return_value = None
                process.send_signal.side_effect = signal

            def request(_port, path, **_kwargs):
                if path == "/health":
                    return b"OK"
                if path == "/ready":
                    raise HTTPError("loopback", 503, "Unavailable", None, None)
                self.assertEqual("/metrics", path)
                if state["paused"]:
                    state["outage_reads"] += 1
                    # Metric export can precede the corresponding log write.
                    log_path.write_text(notification * min(state["outage_reads"], 2))
                    available = 0
                else:
                    state["healthy_reads"] += 1
                    available = int(state["healthy_reads"] >= 2)
                return (f"slimdata_raft_has_leader {available}\n"
                        f"slimdata_raft_has_consensus {available}\n"
                        f"slimdata_raft_consensus_unavailable_duration_seconds {0 if available else 70}\n"
                        "slimdata_raft_last_log_index 10\n"
                        "slimdata_raft_applied_log_index 10\n"
                        "slimdata_raft_progress_stalled 0\n").encode()

            with patch.object(cluster, "leader", return_value=0), \
                    patch.object(cluster, "ready"), \
                    patch.object(cluster, "write"), \
                    patch.object(cluster, "verify"), \
                    patch.object(HARNESS, "request", side_effect=request), \
                    patch.object(HARNESS.time, "sleep"):
                cluster.idle_quorum_loss()

    def test_verification_shares_one_deadline_across_all_keys_and_nodes(self):
        now = 0.0
        timeouts = []

        def read(_port, _path, timeout):
            nonlocal now
            timeouts.append(timeout)
            now += timeout
            return b"value"

        with tempfile.TemporaryDirectory() as output:
            cluster = HARNESS.Cluster(Path(output))
            cluster.processes = {0: None, 1: None, 2: None}
            cluster.expected = {str(i): b"value" for i in range(100)}
            with patch.object(HARNESS.time, "monotonic", side_effect=lambda: now), \
                    patch.object(HARNESS, "request", side_effect=read):
                with self.assertRaisesRegex(AssertionError, "phase deadline"):
                    cluster.verify("bounded")
        self.assertEqual(HARNESS.DEADLINE, now)
        self.assertTrue(all(0 < timeout <= 5 for timeout in timeouts))

    def test_wrong_values_and_server_errors_are_not_retried(self):
        for response in (b"wrong", HTTPError("loopback", 503, "Unavailable", None, None)):
            with self.subTest(response=response), tempfile.TemporaryDirectory() as output:
                cluster = HARNESS.Cluster(Path(output))
                cluster.processes = {0: None}
                cluster.expected = {"key": b"expected"}
                with patch.object(HARNESS, "request") as request:
                    if isinstance(response, Exception):
                        request.side_effect = response
                    else:
                        request.return_value = response
                    with self.assertRaises(AssertionError):
                        cluster.verify("invalid")
                    self.assertEqual(1, request.call_count)

    def test_failed_write_is_not_reported_as_an_acknowledgement(self):
        pending = Future()
        error = TimeoutError("write failed")
        pending.set_exception(error)
        with self.assertRaisesRegex(AssertionError, "Write failed before quorum recovery") as raised:
            HARNESS.assert_write_not_acknowledged(pending)
        self.assertIs(error, raised.exception.__cause__)

    def test_successful_minority_write_fails_and_pending_write_is_allowed(self):
        pending = Future()
        HARNESS.assert_write_not_acknowledged(pending)
        pending.set_result(None)
        with self.assertRaisesRegex(AssertionError, "minority acknowledged"):
            HARNESS.assert_write_not_acknowledged(pending)


if __name__ == "__main__":
    unittest.main()
