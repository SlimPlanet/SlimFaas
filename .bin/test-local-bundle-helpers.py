#!/usr/bin/env python3
"""Regress bundle startup and cleanup failures without sockets or real delays."""
import http.client
import importlib.util
import io
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
            urllib.error.HTTPError("http://127.0.0.1:30020/ready", 503, "Not ready", {}, io.BytesIO()),
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


class FinishedJobCleanupTests(unittest.TestCase):
    def test_deletes_only_the_observed_job_once(self):
        request = Mock(return_value=(200, b""))
        bundle.cleanup_finished_job(request, "smoke-job")
        request.assert_called_once_with("/job/fibonacci/smoke-job", "DELETE")

    def test_already_absent_job_does_not_fail_cleanup(self):
        request = Mock(side_effect=urllib.error.HTTPError(
            "http://127.0.0.1:30020/job/fibonacci/smoke-job", 404, "Not found", {}, io.BytesIO()))
        bundle.cleanup_finished_job(request, "smoke-job")
        request.assert_called_once_with("/job/fibonacci/smoke-job", "DELETE")

    def test_other_cleanup_failures_propagate_without_replaying_mutations(self):
        errors = [
            urllib.error.HTTPError("http://127.0.0.1:30020/job/fibonacci/smoke-job", 500, "Server error", {}, io.BytesIO()),
            urllib.error.HTTPError("http://127.0.0.1:30020/job/fibonacci/smoke-job", 403, "Forbidden", {}, io.BytesIO()),
            ConnectionAbortedError(10053, "Acceptance unknown")
        ]
        for error in errors:
            with self.subTest(error=error):
                if isinstance(error, urllib.error.HTTPError):
                    self.addCleanup(error.close)
                request = Mock(side_effect=error)
                with self.assertRaises(type(error)):
                    bundle.cleanup_finished_job(request, "smoke-job")
                request.assert_called_once()


class ShutdownTests(unittest.TestCase):
    def setUp(self):
        self.now = 0.0
        self.clock = patch.object(bundle.time, "monotonic", side_effect=lambda: self.now)
        self.sleep = patch.object(bundle.time, "sleep", side_effect=self.advance)
        self.clock.start()
        self.sleep.start()
        self.addCleanup(self.clock.stop)
        self.addCleanup(self.sleep.stop)

    def advance(self, seconds):
        self.now += seconds

    def test_stop_signals_the_supervisor_and_waits_for_it(self):
        process = Mock(pid=4242)
        process.poll.return_value = None
        with patch.object(bundle.subprocess, "run") as run:
            bundle.stop_supervisor(process)
        process.send_signal.assert_called_once()
        process.wait.assert_called_once_with(timeout=45)
        run.assert_not_called()

    def test_stop_force_kills_the_tree_only_after_the_deadline(self):
        process = Mock(pid=4242)
        process.poll.return_value = None
        process.wait.side_effect = [bundle.subprocess.TimeoutExpired("slimfaas", 45), 0]
        with patch.object(bundle.subprocess, "run") as run, patch.object(bundle.os, "killpg", create=True) as killpg:
            bundle.stop_supervisor(process)
        self.assertEqual(process.wait.call_count, 2)
        if bundle.os.name == "nt":
            run.assert_called_once_with(["taskkill", "/PID", "4242", "/T", "/F"], check=False)
        else:
            killpg.assert_called_once_with(4242, bundle.signal.SIGKILL)

    def test_stop_leaves_an_exited_supervisor_alone(self):
        process = Mock()
        process.poll.return_value = 0
        bundle.stop_supervisor(process)
        process.send_signal.assert_not_called()

    def test_clean_shutdown_accepts_exit_code_zero(self):
        bundle.ensure_clean_shutdown(Mock(returncode=0), Mock())

    def test_shutdown_failure_prints_the_log_and_fails(self):
        log = Mock()
        log.read_text.return_value = "[node/slimfaas-2] stop failed"
        with patch("builtins.print") as printed, self.assertRaisesRegex(RuntimeError, "exited with code 1 while stopping"):
            bundle.ensure_clean_shutdown(Mock(returncode=1), log)
        printed.assert_called_once_with("[node/slimfaas-2] stop failed")

    def test_removal_retries_while_a_file_is_still_held(self):
        held = PermissionError(13, "The process cannot access the file", "wal/data/0")
        with patch.object(bundle.shutil, "rmtree", side_effect=[held, held, None]) as rmtree:
            bundle.remove_tree("bundle")
        self.assertEqual(rmtree.call_args_list, [call("bundle")] * 3)
        self.assertEqual(self.now, .5)

    def test_removal_gives_up_at_the_deadline_naming_the_file(self):
        held = PermissionError(13, "The process cannot access the file", "wal/data/0")
        with patch.object(bundle.shutil, "rmtree", side_effect=held) as rmtree:
            with self.assertRaisesRegex(RuntimeError, "still in use after shutdown: wal/data/0") as raised:
                bundle.remove_tree("bundle", timeout=1)
        self.assertIs(raised.exception.__cause__, held)
        self.assertEqual(self.now, 1)
        self.assertEqual(rmtree.call_count, 5)

    def test_removal_does_not_retry_other_errors(self):
        with patch.object(bundle.shutil, "rmtree", side_effect=FileNotFoundError("gone")) as rmtree:
            with self.assertRaises(FileNotFoundError):
                bundle.remove_tree("bundle")
        rmtree.assert_called_once()


if __name__ == "__main__":
    unittest.main(verbosity=2)
