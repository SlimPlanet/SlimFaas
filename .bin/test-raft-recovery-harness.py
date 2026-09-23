#!/usr/bin/env python3
"""Deterministic failure checks for the native Raft recovery harness."""

from concurrent.futures import Future
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError


SPEC = importlib.util.spec_from_file_location(
    "raft_recovery", Path(__file__).with_name("test-slimdata-raft-recovery.py"))
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class RecoveryHarnessTests(unittest.TestCase):
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
