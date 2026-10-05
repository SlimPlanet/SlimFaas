#!/usr/bin/env python3
"""Regress bundle startup and cleanup failures without sockets or real delays."""
import http.client
import importlib.util
import io
import os
import shutil
import subprocess
import tempfile
import unittest
import urllib.error
import zipfile
from pathlib import Path
from unittest.mock import Mock, call, patch

spec = importlib.util.spec_from_file_location("local_bundle", Path(__file__).with_name("test-local-bundle.py"))
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


class BundleConfigurationTests(unittest.TestCase):
    def test_default_entrypoint_and_manifests_remain_compatible(self):
        args = bundle.parse_arguments(["demo.zip"])
        self.assertEqual(args.base_url, "http://127.0.0.1:30020")
        self.assertEqual(args.manifest_overlay, [])

    def test_multiple_overlay_paths_with_spaces_and_custom_entrypoint(self):
        args = bundle.parse_arguments(["demo.zip", "--manifest-overlay", "ports with spaces.yaml",
                                       "--manifest-overlay", "state.yaml", "--base-url", "http://127.0.0.1:31020/"])
        self.assertEqual(args.manifest_overlay, [Path("ports with spaces.yaml"), Path("state.yaml")])
        self.assertEqual(args.base_url, "http://127.0.0.1:31020/")

    def test_checks_every_shipped_shell_script_for_crlf(self):
        scripts = ("start.sh", "demo/smoke-tour.sh", "demo/async-scale-tour.sh")
        for invalid in (None,) + scripts:
            with self.subTest(invalid=invalid), io.BytesIO() as stream:
                with zipfile.ZipFile(stream, "w") as archive:
                    for name in scripts:
                        archive.writestr(name, b"#!/bin/sh\r\n" if name == invalid else b"#!/bin/sh\n")
                stream.seek(0)
                with zipfile.ZipFile(stream) as archive:
                    if invalid is None:
                        bundle.validate_bundle_scripts(archive)
                    else:
                        with self.assertRaisesRegex(ValueError, invalid):
                            bundle.validate_bundle_scripts(archive)


class TourScriptTests(unittest.TestCase):
    def setUp(self):
        self.bash = shutil.which("bash")
        if os.name == "nt":
            git_bash = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
            if git_bash.is_file():
                self.bash = str(git_bash)
        if not self.bash:
            self.skipTest("Bash is not installed")
        self.repository = Path(__file__).resolve().parent.parent

    def test_missing_tools_fail_before_any_http_request(self):
        for script in ("smoke-tour.sh", "async-scale-tour.sh"):
            for missing in ("curl", "jq"):
                with self.subTest(script=script, missing=missing):
                    fake_curl = "curl() { echo 'HTTP must not run'; exit 99; }; " if missing == "jq" else ""
                    probe = "PATH=''; " + fake_curl + 'source "$1"'
                    result = subprocess.run([self.bash, "--noprofile", "--norc", "-c", probe, "test",
                                             (self.repository / "demo" / script).as_posix()],
                                            capture_output=True, text=True, check=False)
                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertEqual(result.stdout, "")
                    self.assertIn("Missing required tool: " + missing, result.stderr)

    def state_probe(self, script, mocks, function="read_state"):
        content = (self.repository / "demo" / script).read_text(encoding="utf-8")
        start = content.index(function + "() {")
        end = content.index("\n}\n", start) + 3
        with tempfile.TemporaryDirectory(prefix="Tour state with spaces ") as temporary:
            probe = 'BASE_URL=http://localhost; TOUR_TMP="$1"; SCALE_TMP="$1";\n' + mocks + content[start:end] + "\n" + function + "\n"
            return subprocess.run([self.bash, "--noprofile", "--norc", "-e", "-c", probe, "test",
                                   Path(temporary).as_posix()], capture_output=True, text=True, check=False)

    def test_missing_snapshot_is_retried_and_activity_frames_are_skipped(self):
        state = '{"Functions":[{"Name":"fibonacci1","NumberRequested":1,"NumberReady":1}],"Queues":[{"Name":"fibonacci1","Length":0}]}'
        mocks = "expected_state='" + state + "'\n" + '''calls=0
curl() {
  calls=$((calls + 1))
  if [[ $calls == 1 ]]; then return 28; fi
  printf 'event: activity\\r\\ndata: {"Type":"request_in"}\\r\\n\\r\\nevent: state\\r\\ndata: %s\\r\\n\\r\\n' "$expected_state"
  return 28
}
jq() {
  [[ $(< "${!#}") == "$expected_state"* ]] || return 4
  case "$2" in *NumberRequested*|*NumberReady*) echo 1 ;; *Length*) echo 0 ;; esac
  return 0
}
sleep() { :; }
'''
        for script in ("smoke-tour.sh", "async-scale-tour.sh"):
            with self.subTest(script=script):
                result = self.state_probe(script, mocks)
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_snapshot_has_a_bounded_actionable_failure(self):
        mocks = '''curl() { return 28; }
jq() { return 4; }
sleep() { SECONDS=$((SECONDS + 15)); }
'''
        for script in ("smoke-tour.sh", "async-scale-tour.sh"):
            with self.subTest(script=script):
                result = self.state_probe(script, mocks)
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("SSE state snapshot received within 15 seconds", result.stderr)

    def test_callback_waits_past_initial_empty_queue_snapshots(self):
        mocks = '''observations=0
read_state() { observations=$((observations + 1)); echo "snapshot-$observations"; }
jq() { [[ $observations == 3 ]]; }
curl() { echo 'Unexpected request replay' >&2; exit 99; }
'''
        result = self.state_probe("smoke-tour.sh", mocks, "wait_queue_nonempty")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ["snapshot-1", "snapshot-2", "snapshot-3"])

    def test_callback_missing_from_queue_fails_with_a_deadline(self):
        mocks = '''read_state() { SECONDS=$((SECONDS + 15)); }
jq() { return 1; }
curl() { echo 'Unexpected request replay' >&2; exit 99; }
'''
        result = self.state_probe("smoke-tour.sh", mocks, "wait_queue_nonempty")
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("Deferred callback was not observed in the fibonacci1 queue within 15 seconds", result.stderr)


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


if __name__ == "__main__":
    unittest.main(verbosity=2)
