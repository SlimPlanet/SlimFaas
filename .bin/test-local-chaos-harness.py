#!/usr/bin/env python3
"""Protect the Local campaign's failure/accounting and payload integrity checks."""

import importlib.util
import io
import itertools
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import Mock, call, patch


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ROOT = Path(__file__).resolve().parents[1]
HARNESS = load("local_chaos", ROOT / ".bin/test-slimdata-local-chaos.py")
FIXTURE = load("chaos_function", ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py")


class CampaignSafetyTests(unittest.TestCase):
    def test_failed_resume_stops_continuous_traffic_before_propagating_identity_error(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            started, completed = threading.Event(), threading.Event()
            stopping_events, failures = [], []

            def traffic(stopping):
                stopping_events.append(stopping)
                started.set()
                stopping.wait()

            def monitor(*_args):
                self.assertTrue(started.wait(2))

            def pause(_pid, paused):
                if not paused:
                    raise RuntimeError("Stale runtime identity")

            def exercise():
                try:
                    campaign.absence("follower", 0)
                except BaseException as error:
                    failures.append(error)
                finally:
                    completed.set()

            with patch.object(campaign, "ready"), patch.object(campaign, "leader", return_value=0), \
                    patch.object(campaign, "node_pid", return_value=111), \
                    patch.object(campaign, "traffic", side_effect=traffic), \
                    patch.object(campaign, "monitor_absence", side_effect=monitor), \
                    patch.object(campaign, "pause", side_effect=pause):
                thread = threading.Thread(target=exercise, daemon=True)
                thread.start()
                try:
                    self.assertTrue(completed.wait(3), "Recovery error left the continuous traffic producer running")
                    self.assertTrue(stopping_events[0].is_set())
                    self.assertEqual(["Stale runtime identity"], [str(error) for error in failures])
                finally:
                    for event in stopping_events:
                        event.set()
                    thread.join(5)
                self.assertFalse(thread.is_alive())

    def test_failed_network_fault_setup_stops_continuous_traffic(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            completed = threading.Event()
            stopping_events, failures = [], []

            def traffic(stopping):
                stopping_events.append(stopping)
                stopping.wait()

            def exercise():
                try:
                    campaign.partitions()
                except BaseException as error:
                    failures.append(error)
                finally:
                    completed.set()

            with patch.object(campaign, "seed"), patch.object(campaign, "ready"), \
                    patch.object(campaign, "leader", return_value=0), \
                    patch.object(campaign, "traffic", side_effect=traffic), \
                    patch.object(HARNESS.sys, "platform", "linux"), \
                    patch.object(HARNESS.os, "geteuid", return_value=0), \
                    patch.object(HARNESS.os, "readlink", side_effect=lambda path: path), \
                    patch.object(HARNESS.subprocess, "run", side_effect=RuntimeError("Fault setup rejected")) as control:
                thread = threading.Thread(target=exercise, daemon=True)
                thread.start()
                try:
                    self.assertTrue(completed.wait(3), "Fault setup error left traffic running")
                    self.assertTrue(stopping_events[0].is_set())
                    self.assertEqual(["Fault setup rejected"], [str(error) for error in failures])
                    self.assertEqual(1, control.call_count, "Do not remove a rule that was never installed")
                finally:
                    for event in stopping_events:
                        event.set()
                    thread.join(5)
                self.assertFalse(thread.is_alive())

    def test_failed_resume_still_stops_owned_supervisor_and_closes_its_log(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.suspended = {111}
            campaign.log = Mock()
            log = campaign.log
            with patch.object(campaign, "pause", side_effect=RuntimeError("Recovery controller unavailable")), \
                    patch.object(campaign, "stop_supervisor") as stop, \
                    patch.object(campaign, "ports_released", return_value=True):
                with self.assertRaisesRegex(RuntimeError, "Recovery controller unavailable"):
                    campaign.stop()
            stop.assert_called_once()
            log.close.assert_called_once()
            self.assertIsNone(campaign.log)

    def test_counter_contracts_keep_retained_values_and_can_run_twice(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            original = "/data/sets/chaos-counter"
            campaign.expected[original] = HARNESS.sha256(b"32.5")
            replies, paths, gate = {}, set(), threading.Lock()
            def submit(_kind, path, _body):
                key, _operation = path.rsplit("/", 1)
                with gate:
                    self.assertNotEqual(original, key, "Counter exercise reused a retained float value")
                    paths.add(key)
                    values = replies.setdefault(key, iter(list(range(1, 33)) + list(range(31, -1, -1)) + [42, 32, 32.5]))
                    return str(next(values)).encode(), "id"
            with patch.object(campaign, "require_mutation", side_effect=submit):
                campaign.counter_contracts()
                campaign.counter_contracts()
            self.assertEqual(2, len(paths))
            self.assertEqual({original} | paths, set(campaign.expected))
            self.assertEqual(HARNESS.sha256(b"32.5"), campaign.expected[original])

    def test_unknown_counter_can_commit_between_sequential_node_reads(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1]
            campaign.unknown = {str(index): {"kind": "counter"} for index in range(2)}
            replies = [(200, {}, str(number).encode()) for number in (2, 3, 3, 3, 3, 3)]
            with patch.object(HARNESS, "request", side_effect=replies) as send:
                campaign.verify_counters()
            self.assertEqual(6, send.call_count)
            observations = [json.loads(line) for line in campaign.client.path.read_text().splitlines()]
            self.assertEqual([[2, 3, 3], [3, 3, 3]], [row["values"] for row in observations])

    def test_counter_divergence_without_unknown_writes_fails_immediately(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1]
            with patch.object(HARNESS, "request", side_effect=[(200, {}, value) for value in (b"1", b"2", b"2")]) as send:
                with self.assertRaisesRegex(AssertionError, "Mixed counter invariant"):
                    campaign.verify_counters()
            self.assertEqual(3, send.call_count)

    def test_counter_bounds_and_duplicate_confirmed_results_fail_immediately(self):
        for confirmed, value in (([1, 2], b"1"), ([1], b"3"), ([1, 1], b"2")):
            with self.subTest(confirmed=confirmed, value=value), tempfile.TemporaryDirectory() as output:
                campaign = self.campaign(output)
                campaign.counter_results = confirmed
                campaign.unknown = {"unknown": {"kind": "counter"}}
                with patch.object(HARNESS, "request", return_value=(200, {}, value)) as send:
                    with self.assertRaisesRegex(AssertionError, "Mixed counter invariant"):
                        campaign.verify_counters()
                self.assertEqual(3, send.call_count)

    def test_counter_cannot_fall_below_the_highest_confirmed_increment_response(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1, 3]
            campaign.unknown = {"unknown": {"kind": "counter"}}
            with patch.object(HARNESS, "request", return_value=(200, {}, b"2")) as send:
                with self.assertRaisesRegex(AssertionError, "Mixed counter invariant"):
                    campaign.verify_counters()
            self.assertEqual(3, send.call_count)

    def test_counter_regression_is_not_hidden_by_unknown_writes(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1]
            campaign.unknown = {str(index): {"kind": "counter"} for index in range(2)}
            replies = [(200, {}, str(number).encode()) for number in (2, 3, 3, 1, 3, 3)]
            with patch.object(HARNESS, "request", side_effect=replies) as send:
                with self.assertRaisesRegex(AssertionError, "Mixed counter invariant"):
                    campaign.verify_counters()
            self.assertEqual(6, send.call_count)

    def test_counter_must_converge_within_the_existing_phase_deadline(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1]
            campaign.unknown = {"unknown": {"kind": "counter"}}
            campaign.options.recovery_timeout = .005
            replies = itertools.cycle([(200, {}, value) for value in (b"1", b"2", b"2")])
            with patch.object(HARNESS, "request", side_effect=replies):
                with self.assertRaisesRegex(AssertionError, "mixed counter convergence|phase deadline"):
                    campaign.verify_counters()

    def test_counter_server_errors_fail_immediately(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.counter_results = [1]
            campaign.unknown = {"unknown": {"kind": "counter"}}
            with patch.object(HARNESS, "request", return_value=(503, {}, b"unavailable")) as send:
                with self.assertRaisesRegex(AssertionError, "Mixed counter is unavailable"):
                    campaign.verify_counters()
            self.assertEqual(3, send.call_count)

    def test_lease_probe_failure_resumes_the_owned_follower_and_propagates(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.client = Mock()
            clock = itertools.count()
            with patch.object(campaign, "leader", return_value=0), \
                    patch.object(campaign, "node_pid", return_value=111), \
                    patch.object(HARNESS.time, "monotonic", side_effect=lambda: next(clock)), \
                    patch.object(HARNESS.time, "sleep"), \
                    patch.object(HARNESS, "suspend") as change_state, \
                    patch.object(campaign, "require_mutation", side_effect=[None] * 4 + [AssertionError("probe failed")]) as send:
                with self.assertRaisesRegex(AssertionError, "probe failed"):
                    campaign.lease_burst()
            self.assertEqual(5, send.call_count)
            self.assertEqual([call(111, True), call(111, False)], change_state.call_args_list)
            self.assertFalse(campaign.suspended)

    def test_fault_pid_uses_supervisor_output_after_optional_node_log_loses_a_restart(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.generation = 1
            logs = campaign.state_root / "logs"
            logs.mkdir(parents=True)
            (logs / "slimfaas-0.log").write_text("[node/slimfaas-0] started pid=111\n")
            (campaign.output / "supervisor-0.log").write_text(
                "[node/slimfaas-0] started pid=111\n[node/slimfaas-1] started pid=222\n"
                "[node/slimfaas-0] started pid=333\n")
            self.assertEqual(333, campaign.node_pid(0))

    def test_minority_write_success_is_allowed_only_after_quorum_restoration(self):
        with self.assertRaisesRegex(AssertionError, "without quorum"):
            HARNESS.assert_minority_result((200, {}, b"", "id"), 9.999, 10)
        HARNESS.assert_minority_result((200, {}, b"", "id"), 10.001, 10)
        HARNESS.assert_minority_result((503, {}, b"", "id"), 9, 10)

    def campaign(self, output):
        options = HARNESS.parse_arguments(["--output", str(Path(output) / "run"),
                                           "--runtime", str(Path(__file__))])
        return HARNESS.Campaign(options)

    def test_unknown_write_is_journaled_once_without_automatic_mutation_retry(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            with patch.object(HARNESS, "request", side_effect=TimeoutError("response lost")) as send:
                status, _, _, identifier = campaign.mutate("counter", "/data/sets/counter/incr", b"")
            self.assertIsNone(status)
            self.assertNotIn(identifier, campaign.accepted)
            self.assertIn(identifier, campaign.unknown)
            self.assertEqual(1, send.call_count)
            events = [json.loads(line)["event"] for line in campaign.client.path.read_text().splitlines()]
            self.assertEqual(["intent", "unknown"], events)

    def test_server_error_can_be_committed_and_must_remain_unknown(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            with patch.object(HARNESS, "request", return_value=(503, {}, b"unavailable")):
                _, _, _, identifier = campaign.mutate("set", "/data/sets/value", b"value")
            self.assertIn(identifier, campaign.unknown)
            self.assertNotIn(identifier, campaign.accepted)

    def test_convergence_rejects_wrong_bytes_and_server_errors_without_retry(self):
        for response in ((200, {}, b"corrupt"), (503, {}, b"unavailable")):
            with self.subTest(response=response), tempfile.TemporaryDirectory() as output:
                campaign = self.campaign(output)
                campaign.expected["/data/sets/value"] = HARNESS.sha256(b"expected")
                with patch.object(HARNESS, "request", return_value=response) as send:
                    with self.assertRaisesRegex(AssertionError, "Convergence failure"):
                        campaign.verify()
                    self.assertEqual(1, send.call_count)

    def test_reconciliation_rejects_a_function_receipt_with_different_content(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            campaign.accepted["accepted"] = {"kind": "async", "sha256": HARNESS.sha256(b"ok"), "bytes": 2}
            FIXTURE.Journal(campaign.receipts).append({"id": "accepted", "event": "completed", "sha256": "bad",
                                                      "expected_sha256": "bad", "bytes": 2, "status": 200})
            with self.assertRaisesRegex(AssertionError, "Receipt integrity failure"):
                campaign.reconcile()

    def test_partial_receipt_is_not_proof_of_success_and_malformed_complete_receipt_fails(self):
        with tempfile.TemporaryDirectory() as output:
            path = Path(output) / "receipts-1.jsonl"
            path.write_bytes(b'{"event":"completed"}')
            self.assertEqual([], HARNESS.read_receipts(Path(output)))
            path.write_bytes(b'{not-json}\n')
            with self.assertRaises(json.JSONDecodeError):
                HARNESS.read_receipts(Path(output))

    def test_producer_failure_stops_other_producers_and_propagates(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            stopping = threading.Event()
            with patch.object(campaign, "mutate", side_effect=AssertionError("producer failed")), \
                    self.assertRaisesRegex(AssertionError, "producer failed"):
                campaign.traffic(stopping)
            self.assertTrue(stopping.is_set())

    def test_existing_run_directory_is_never_reused_or_cleaned(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            marker = campaign.output / "persistent-proof"
            marker.write_bytes(b"retain")
            with self.assertRaises(FileExistsError):
                self.campaign(output)
            self.assertEqual(b"retain", marker.read_bytes())

    def test_occupied_port_prevents_starting_a_second_cluster_on_retained_volumes(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            with patch.object(HARNESS.socket, "socket") as probe, patch.object(HARNESS.subprocess, "Popen") as start:
                probe.return_value.__enter__.return_value.connect_ex.return_value = 0
                with self.assertRaisesRegex(RuntimeError, "already has a listener"):
                    campaign.start()
            start.assert_not_called()

    def test_resume_reconstructs_confirmed_values_and_keeps_the_failed_report(self):
        with tempfile.TemporaryDirectory() as output:
            campaign = self.campaign(output)
            with patch.object(HARNESS, "request", return_value=(200, {}, b'"value"')):
                campaign.mutate("set", "/data/sets/value", b"confirmed")
            with patch.object(HARNESS, "request", return_value=(200, {}, b"32.5")):
                campaign.mutate("counter-check", "/data/sets/retained-counter/incrbyfloat?by=0.5", b"")
            (campaign.output / "parameters.json").write_text(json.dumps({"arguments": {"port_base": campaign.entry}}))
            (campaign.output / "report.json").write_text(json.dumps({"passed": False, "results": []}))
            campaign.options.resume = True
            resumed = HARNESS.Campaign(campaign.options)
            self.assertEqual(HARNESS.sha256(b"confirmed"), resumed.expected["/data/sets/value"])
            self.assertEqual(HARNESS.sha256(b"32.5"), resumed.expected["/data/sets/retained-counter"])
            self.assertEqual(1, len(list(campaign.output.glob("report-before-resume-*.json"))))
            self.assertFalse(json.loads((campaign.output / "report.json").read_text())["passed"])


class BodyIntegrityTests(unittest.TestCase):
    def test_known_length_and_chunked_bodies_have_the_same_checksum(self):
        body = b"\x00\xffpayload\r\n"
        expected = (len(body), HARNESS.sha256(body))
        self.assertEqual(expected, FIXTURE.hash_body(io.BytesIO(body), len(body)))
        encoded = f"{len(body):x};extension=ok\r\n".encode() + body + b"\r\n0\r\nTrailer: value\r\n\r\n"
        self.assertEqual(expected, FIXTURE.hash_body(io.BytesIO(encoded), chunked=True))

    def test_truncated_transfers_never_produce_a_success_checksum(self):
        for stream, length, chunked in ((b"abc", 4, False), (b"4\r\nabc", None, True),
                                        (b"0\r\n", None, True)):
            body_stream = io.BytesIO(stream)
            with self.subTest(stream=stream), self.assertRaises(EOFError):
                FIXTURE.hash_body(body_stream, length, chunked)


if __name__ == "__main__":
    unittest.main()
