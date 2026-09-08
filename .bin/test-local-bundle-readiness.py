#!/usr/bin/env python3
"""Regress startup connection failures without real sockets or wall-clock delays."""
import http.client
import importlib.util
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import Mock, call, patch

spec = importlib.util.spec_from_file_location("local_bundle", Path(__file__).with_name("test-local-bundle.py"))
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.now = 0.0
        self.process = Mock()
        self.process.poll.return_value = None
        self.clock = patch.object(bundle.time, "monotonic", side_effect=lambda: self.now)
        self.sleep = patch.object(bundle.time, "sleep", side_effect=self.advance)
        self.clock.start()
        self.sleep.start()
        self.addCleanup(self.clock.stop)
        self.addCleanup(self.sleep.stop)

    def advance(self, seconds):
        self.now += seconds

    def test_startup_retries_aborted_reset_and_unavailable_connections(self):
        errors = [
            ConnectionAbortedError(10053, "An established connection was aborted"),
            ConnectionResetError(10054, "Connection reset by peer"),
            http.client.RemoteDisconnected("Remote end closed connection without response"),
            urllib.error.URLError(ConnectionRefusedError("Not listening yet")),
            urllib.error.HTTPError("http://127.0.0.1:30020/ready", 503, "Not ready", {}, None),
            TimeoutError("Still starting")
        ]
        for error in errors:
            with self.subTest(error=type(error).__name__):
                request = Mock(side_effect=[error, (200, b"READY")])
                bundle.wait_for_ready(request, self.process)
                self.assertEqual(request.call_args_list, [call("/ready", timeout=5)] * 2)

    def test_waits_for_successful_readiness_status(self):
        request = Mock(side_effect=[(503, b"Not ready"), (200, b"READY")])
        bundle.wait_for_ready(request, self.process)
        self.assertEqual(request.call_count, 2)

    def test_persistent_connection_failure_expires_with_original_cause(self):
        failure = ConnectionAbortedError(10053, "Connection aborted")
        request = Mock(side_effect=failure)
        with self.assertRaisesRegex(TimeoutError, "never became ready") as raised:
            bundle.wait_for_ready(request, self.process, timeout=1)
        self.assertIs(raised.exception.__cause__, failure)
        self.assertEqual(self.now, 1)
        self.assertEqual([args.kwargs["timeout"] for args in request.call_args_list], [1, .75, .5, .25])

    def test_each_probe_is_limited_to_remaining_startup_budget(self):
        def timed_out_probe(route, timeout):
            self.advance(timeout)
            raise TimeoutError("Read timed out")

        request = Mock(side_effect=timed_out_probe)
        with self.assertRaisesRegex(TimeoutError, "never became ready"):
            bundle.wait_for_ready(request, self.process, timeout=6)
        self.assertEqual(request.call_args_list, [call("/ready", timeout=5), call("/ready", timeout=.75)])
        self.assertEqual(self.now, 6)

    def test_supervisor_exit_stops_polling(self):
        self.process.poll.side_effect = [None, 1]
        request = Mock(side_effect=ConnectionAbortedError(10053, "Connection aborted"))
        with self.assertRaisesRegex(RuntimeError, "supervisor exited"):
            bundle.wait_for_ready(request, self.process)
        request.assert_called_once()

    def test_unexpected_errors_are_not_retried(self):
        request = Mock(side_effect=ValueError("Invalid request"))
        with self.assertRaisesRegex(ValueError, "Invalid request"):
            bundle.wait_for_ready(request, self.process)
        request.assert_called_once()


if __name__ == "__main__":
    unittest.main(verbosity=2)
