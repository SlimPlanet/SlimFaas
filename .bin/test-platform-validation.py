#!/usr/bin/env python3
"""Exercise receipt integrity, process fencing and configurable benchmark endpoints."""

import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch
from types import SimpleNamespace


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
DOTNET_PROBE = load("dotnet_probe", ROOT / ".bin/test-dotnet-sdk-payloads.py")
KUBERNETES_DOTNET = load("kubernetes_dotnet", ROOT / ".bin/test-dotnet-sdk-kubernetes.py")
KUBERNETES_BACKEND = load("kubernetes_backend", ROOT / ".bin/slimdata_kubernetes_backend.py")


class PlatformTests(unittest.TestCase):
    def test_matrix_keeps_kubernetes_controller_output_separate_from_existing_phase(self):
        with tempfile.TemporaryDirectory() as directory:
            phase = Path(directory) / "http-attempt-1" / "1-before"
            phase.parent.mkdir()
            configuration = {"environment": "kubernetes", "prefix": "slimfaas-pr506-test",
                             "kubeconfig": "isolated-config", "before": "historical-runtime",
                             "before_commit": "historical-source", "controller_image": "common-driver"}
            launches = []

            def command(arguments, _log, **_kwargs):
                action = arguments[2]
                destination = Path(arguments[arguments.index("--output") + 1])
                if action == "launch":
                    # The real launcher refuses an existing output directory
                    # before applying any Kubernetes resource.
                    destination.mkdir(parents=True, exist_ok=False)
                    launches.append(destination)
                else:
                    self.assertEqual(launches[0], destination)
                return 0

            with patch.object(MATRIX.shutil, "disk_usage", return_value=SimpleNamespace(free=40 * 1024**3)), \
                    patch.object(MATRIX.subprocess, "check_output", return_value=f"Avail\n{40 * 1024**3}\n"), \
                    patch.object(MATRIX, "invoke", side_effect=command), \
                    patch.object(MATRIX, "export_vm", return_value=phase / "workload"):
                MATRIX.run_one.__wrapped__(configuration, "http", None, None, "before", 1, phase)
            self.assertEqual([phase / "controller"], launches)
            self.assertTrue((phase / "phase.json").is_file())
            self.assertTrue((phase / "execution.json").is_file())

    def test_recovered_runtime_oom_cannot_be_reported_as_successful_endurance(self):
        owned = {"metadata": {"name": "slimfaas-2", "uid": "original-pod",
                              "labels": {"slimfaas-campaign": "owned", "app": "slimfaas"}},
                 "status": {"containerStatuses": [{"name": "runtime", "restartCount": 1,
                    "lastState": {"terminated": {"reason": "OOMKilled", "finishedAt": "original-time"}}}]}}
        with self.assertRaisesRegex(AssertionError, "Unexpected runtime/control OOM"):
            KUBERNETES_BACKEND.assert_no_runtime_ooms([owned], "owned")
        KUBERNETES_BACKEND.assert_no_runtime_ooms([owned], "different-campaign")
        owned["status"]["containerStatuses"][0]["lastState"]["terminated"]["reason"] = "Error"
        KUBERNETES_BACKEND.assert_no_runtime_ooms([owned], "owned")

    def test_kubernetes_sdk_worker_waits_for_service_in_its_own_network_namespace(self):
        resources = KUBERNETES_DOTNET.probe_resources("slimfaas-pr506-owned-sdk", "runtime", "worker")
        worker = next(item for item in resources["items"] if item["metadata"]["name"] == "dotnet-sdk-worker")
        pod = worker["spec"]["template"]["spec"]
        initialization = pod["initContainers"][0]["command"]
        with patch("urllib.request.urlopen", side_effect=[ConnectionRefusedError(),
                Mock(__enter__=Mock(return_value=SimpleNamespace(status=200)), __exit__=Mock(return_value=False))]) as request, \
                patch("time.sleep"):
            exec(compile(initialization[2], "wait-for-service.py", "exec"), {})
        self.assertEqual(2, request.call_count)
        self.assertEqual("http://slimfaas-http:5000/ready", request.call_args.args[0])
        self.assertFalse(any("hostNetwork" in str(item) for item in resources["items"]))
        config = next(item for item in resources["items"] if item["kind"] == "ConfigMap")
        compile(config["data"]["entry.py"], "observer.py", "exec")

    def test_kubernetes_sdk_refuses_an_unowned_namespace_before_any_mutation(self):
        arguments = ["probe", "--kubeconfig", "isolated-config", "--namespace", "slimfaas-pr506-owned-sdk",
                     "--image", "runtime", "--worker-image", "worker", "--output", "unused-output"]
        response = SimpleNamespace(stdout=json.dumps({"metadata": {"labels": {}}}))
        with patch.object(sys, "argv", arguments), \
                patch.object(KUBERNETES_DOTNET.subprocess, "run", return_value=response) as command:
            with self.assertRaisesRegex(RuntimeError, "unowned namespace"):
                KUBERNETES_DOTNET.main()
        self.assertEqual(1, command.call_count)
        self.assertIn("get", command.call_args.args[0])
        self.assertIn("kind-slimfaas-pr506", command.call_args.args[0])

    def test_kubernetes_sdk_refuses_mismatched_runtime_before_any_mutation(self):
        arguments = ["probe", "--kubeconfig", "isolated-config", "--namespace", "slimfaas-pr506-owned-sdk",
                     "--image", "runtime", "--worker-image", "worker", "--output", "unused-output"]
        records = [{"metadata": {"labels": {"slimfaas-campaign": "slimfaas-pr506-owned-sdk"}}},
                   {"status": {"succeeded": 1}},
                   {"spec": {"template": {"spec": {"containers": [{"name": "runtime", "image": "other"}]}}}}]
        with patch.object(sys, "argv", arguments), \
                patch.object(KUBERNETES_DOTNET.subprocess, "run",
                             side_effect=[SimpleNamespace(stdout=json.dumps(row)) for row in records]) as command:
            with self.assertRaisesRegex(RuntimeError, "match the campaign runtime"):
                KUBERNETES_DOTNET.main()
        self.assertTrue(all("get" in invocation.args[0] for invocation in command.call_args_list))

    def test_dotnet_probe_waits_for_service_routing_as_well_as_direct_nodes(self):
        service_attempts = []

        def probe(origin, path):
            if origin == "http://service:5000" and path == "/ready":
                service_attempts.append(origin)
                if len(service_attempts) == 1:
                    raise ConnectionRefusedError()
            return (200, {}, b'[{"Name":"owned","NumberReady":1}]')

        with patch.object(DOTNET_PROBE, "request", side_effect=probe), patch.object(DOTNET_PROBE.time, "sleep"):
            DOTNET_PROBE.wait_for_function(["http://pod:5000"], "owned", base_url="http://service:5000")
        self.assertEqual(2, len(service_attempts))

    def test_dotnet_probe_retries_only_readiness_during_runtime_startup(self):
        responses = [ConnectionRefusedError(), (503, {}, b""), (503, {}, b""), (200, {}, b""),
                     (200, {}, b'[{"Name":"owned","NumberReady":1}]')]
        with patch.object(DOTNET_PROBE, "request", side_effect=responses) as request_mock, \
                patch.object(DOTNET_PROBE.time, "sleep"):
            DOTNET_PROBE.wait_for_function(["http://owned:5000"], "owned")
        self.assertEqual(5, request_mock.call_count)
        self.assertTrue(all(call.args in [("http://owned:5000", "/ready"), ("http://owned:5000", "/status-functions")]
                            for call in request_mock.call_args_list))

    def test_dotnet_worker_log_waits_for_complete_lines_and_keeps_duplicate_observations(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "supervisor.log"
            path.write_bytes(b"")
            observer = DOTNET_PROBE.WorkerLog(path)
            body = b'{"nonce":"actual","padding":"payload"}'
            line = b"[job/owned] [WS]   Corps     : " + body + b"\n"
            path.write_bytes(line[:-5])
            observer.read()
            self.assertEqual(0, observer.position)
            self.assertFalse(observer.observations)
            path.write_bytes(line)
            self.assertEqual([hashlib.sha256(body).hexdigest()], observer.observe("actual", timeout=0))
            with path.open("ab") as stream:
                stream.write(line)
            observer.read()
            self.assertEqual(2, len(observer.observations["actual"]))

    def test_websocket_profiles_keep_distinct_durable_registrations_and_restore_their_own_identity(self):
        module = SimpleNamespace(SlimFaasClient=object,
            SlimFaasClientConfig=lambda **values: SimpleNamespace(**values), SubscribeStreamConfig=lambda value: value)
        with tempfile.TemporaryDirectory() as directory, patch.dict(sys.modules, {"slimfaas_client": module}):
            root = Path(directory)
            campaign = SimpleNamespace(output=root, receipts=root / "receipts", accepted={}, options=SimpleNamespace(concurrency=4))
            default = WORKLOAD.LOCAL.WebSocketCampaign(campaign)
            timeout = WORKLOAD.LOCAL.WebSocketCampaign(campaign, profile="timeout")
            self.assertNotEqual(default.function, timeout.function)
            self.assertEqual(default.function, WORKLOAD.LOCAL.WebSocketCampaign(campaign).function)
            self.assertEqual(timeout.function, WORKLOAD.LOCAL.WebSocketCampaign(campaign, profile="timeout").function)
            (root / "websocket-timeout.json").unlink()
            campaign.accepted = {"old": {"kind": "async", "phase": "websocket-owner-timeout",
                "path": "/async-function/chaos-ws-legacy/consume"}}
            self.assertEqual("chaos-ws-legacy", WORKLOAD.LOCAL.WebSocketCampaign(campaign, profile="timeout").function)

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
                        WORKLOAD.verify_receipts(output, root, timeout=0)
                else:
                    WORKLOAD.verify_receipts(output, root, timeout=0)
                    self.assertTrue(json.loads(output.with_suffix(".receipts.json").read_text())["passed"])

    def test_receipt_reconciliation_observes_a_real_late_delivery_and_ignores_partial_lines(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "operations.json"
            output.write_text(json.dumps({"report": {"KeyPrefix": "load", "PayloadBytes": 4}, "samples": [
                {"operation": "Async", "Success": True, "Worker": 1, "Sequence": 2}]}))
            receipt = {"event": "completed", "id": "load-async-1-2", "bytes": 4,
                       "sha256": hashlib.sha256(b"abcd").hexdigest(), "status": 200}
            path = root / "receipts.jsonl"
            path.write_text('{"event":')
            def deliver(_seconds):
                FIXTURE.Journal(root).append(receipt)
            with patch.object(WORKLOAD.time, "sleep", side_effect=deliver) as wait:
                WORKLOAD.verify_receipts(output, root, timeout=1)
                wait.assert_called_once()
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
            self.assertNotEqual(json.loads(first.path.read_text())["writer_id"],
                                json.loads(second.path.read_text())["writer_id"])
            self.assertEqual(os.getpid(), json.loads(first.path.read_text())["pid"])

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
        runtime = ROOT / "tools/SlimFaas.MemoryLab/bin/Release/net11.0/SlimFaas.MemoryLab.dll"
        if not runtime.exists():
            self.skipTest("Build MemoryLab Release before running endpoint integration checks")
        for urls in ("file:///tmp", "http://user:password@localhost", "http://localhost/prefix", ","):
            result = subprocess.run(["dotnet", str(runtime), "load", "--node-urls", urls], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("node", result.stderr.lower())

    def test_memorylab_custom_function_and_node_urls_preserve_async_integrity_headers(self):
        runtime = ROOT / "tools/SlimFaas.MemoryLab/bin/Release/net11.0/SlimFaas.MemoryLab.dll"
        if not runtime.exists():
            self.skipTest("Build MemoryLab Release before running endpoint integration checks")
        receipts = []

        class Target(BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                receipts.append((self.path, dict(self.headers), body))
                self.send_response(202)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, *_args):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Target) as server:
            running = threading.Thread(target=server.serve_forever)
            running.start()
            try:
                result = subprocess.run(["dotnet", str(runtime), "load", "--scenario", "async",
                    "--node-urls", f"http://127.0.0.1:{server.server_port}",
                    "--function", "custom function", "--duration", "1", "--concurrency", "1",
                    "--payload-bytes", "64", "--target-rps", "5"],
                    capture_output=True, text=True, timeout=15)
            finally:
                server.shutdown()
                running.join()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue(receipts)
        ids = []
        for route, headers, body in receipts:
            self.assertEqual("/async-function/custom%20function/echo", route)
            self.assertEqual(64, len(body))
            self.assertEqual(hashlib.sha256(body).hexdigest(), headers["X-Chaos-Sha256"])
            ids.append(headers["X-Chaos-Id"])
        self.assertEqual(len(ids), len(set(ids)))


if __name__ == "__main__":
    unittest.main()
