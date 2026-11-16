#!/usr/bin/env python3
"""Exercise receipt integrity, process fencing and configurable benchmark endpoints."""

import hashlib
import calendar
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import json
import os
from pathlib import Path
import socket
from socketserver import BaseRequestHandler
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
StorageTests = load("kubernetes_storage_tests", ROOT / ".bin/test-kubernetes-storage.py").StorageTests
KUBERNETES_BACKEND = load("kubernetes_backend", ROOT / ".bin/slimdata_kubernetes_backend.py")
KUBERNETES_CAMPAIGN = load("kubernetes_campaign", ROOT / ".bin/run-kubernetes-campaign.py")
JOURNALED_LOAD = load("journaled_load", ROOT / ".bin/run-journaled-load.py")


class PlatformTests(unittest.TestCase):
    LOG_WINDOW_START = calendar.timegm((2026, 11, 15, 12, 0, 0))

    def test_receipt_fixture_accepts_a_64_connection_burst_before_handlers_start(self):
        class EmptyHandler(BaseRequestHandler):
            def handle(self):
                pass

        server = FIXTURE.create_server(("127.0.0.1", 0), EmptyHandler)
        entered = threading.Event()
        release = threading.Event()
        get_request = server.get_request

        def gated_accept():
            entered.set()
            if not release.wait(10):
                raise TimeoutError("The test did not release its accept barrier")
            return get_request()

        server.get_request = gated_accept
        serving = threading.Thread(target=server.serve_forever, daemon=True)
        serving.start()
        connected = []
        failures = []

        def connect(_):
            try:
                return socket.create_connection(server.server_address, timeout=1), None
            except OSError as error:
                return None, repr(error)

        try:
            connected.append(socket.create_connection(server.server_address, timeout=1))
            self.assertTrue(entered.wait(5))
            with ThreadPoolExecutor(max_workers=63) as pool:
                for connection, error in pool.map(connect, range(63)):
                    if connection is not None:
                        connected.append(connection)
                    else:
                        failures.append(error)
            self.assertEqual([], failures)
            self.assertEqual(64, len(connected))
        finally:
            release.set()
            for connection in connected:
                connection.close()
            server.shutdown()
            serving.join(timeout=5)
            server.server_close()

    def test_runtime_log_audit_rejects_internal_errors_even_with_no_managed_oom(self):
        for message in ("fail: SlimData failed", "crit: host stopped", "Unhandled exception. failure",
                        "2026-11-15T12:00:00.1234567+00:00fail: SlimData failed",
                        "2026-11-15T12:00:00Z crit: host stopped"):
            with self.subTest(message=message), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                self._write_runtime_log_audit_fixture(output, message)
                result = KUBERNETES_CAMPAIGN.audit_runtime_logs(output, self.LOG_WINDOW_START, self.LOG_WINDOW_START + 60)
                self.assertFalse(result["passed"])
                self.assertTrue(result["runtime_log_coverage_complete"])
                self.assertEqual(0, sum(row["managed_oom_log_occurrences"] for row in result["instances"]))

    def test_runtime_log_audit_keeps_bootstrap_errors_separate_and_rejects_missing_history(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            self._write_runtime_log_audit_fixture(output, "healthy")
            result = KUBERNETES_CAMPAIGN.audit_runtime_logs(output, self.LOG_WINDOW_START, self.LOG_WINDOW_START + 60)
            self.assertTrue(result["passed"])
            manifest = json.loads((output / "pod-log-coverage.json").read_text())
            manifest["missing_instances"] = [manifest["instances"][0] | {"restart_indices": [0]}]
            (output / "pod-log-coverage.json").write_text(json.dumps(manifest))
            result = KUBERNETES_CAMPAIGN.audit_runtime_logs(output, self.LOG_WINDOW_START, self.LOG_WINDOW_START + 60)
            self.assertFalse(result["passed"])
            self.assertFalse(result["runtime_log_coverage_complete"])

    @staticmethod
    def _write_runtime_log_audit_fixture(output, message):
        rows = []
        instances = []
        for index in range(3):
            path = output / f"node-{index}.log"
            content = ("2026-11-15T11:59:59Z fail: bootstrap has no leader\n"
                       f"2026-11-15T12:00:01Z {message}\n").encode()
            path.write_bytes(content)
            instance = {"pod_uid": f"node-{index}", "container": "runtime", "latest_restart_index": 0}
            instances.append(instance)
            rows.append(instance | {"restart_index": 0, "exit_code": 0, "previous": False,
                "captured_utc": 1, "path": path.name, "sha256": hashlib.sha256(content).hexdigest()})
        (output / "pod-log-coverage.json").write_text(json.dumps({
            "logs": rows, "instances": instances, "missing_instances": []}))

    def test_kubernetes_log_snapshots_keep_replaced_pod_uids_separate(self):
        pod = {"metadata": {"name": "slimfaas-0", "uid": "first-uid"},
               "spec": {"containers": [{"name": "runtime"}]},
               "status": {"containerStatuses": [{"name": "runtime", "restartCount": 0}]}}

        def command(_arguments, **kwargs):
            kwargs["stdout"].write(pod["metadata"]["uid"] + "\n")
            return SimpleNamespace(returncode=0)

        with tempfile.TemporaryDirectory() as directory, \
                patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command):
            output = Path(directory)
            KUBERNETES_CAMPAIGN.collect_pod_logs(["kubectl"], "campaign", [pod], output)
            pod["metadata"]["uid"] = "second-uid"
            manifest = KUBERNETES_CAMPAIGN.collect_pod_logs(["kubectl"], "campaign", [pod], output)
            self.assertEqual(2, len(manifest["logs"]))
            for row in manifest["logs"]:
                self.assertEqual(row["pod_uid"] + "\n", (output / row["path"]).read_text())
            self.assertFalse(manifest["complete"])
            self.assertTrue(manifest["missing_instances"][0]["pod_no_longer_observed"])

    def test_kubernetes_log_poll_retains_each_previous_restart_and_replaces_partial_snapshots(self):
        pod = {"metadata": {"name": "slimfaas-0", "uid": "stable-uid"},
               "spec": {"containers": [{"name": "runtime"}]},
               "status": {"containerStatuses": [{"name": "runtime", "restartCount": 0}]}}

        def command(arguments, **kwargs):
            kwargs["stdout"].write("finished previous instance\n" if "--previous" in arguments else "partial current snapshot\n")
            return SimpleNamespace(returncode=0)

        with tempfile.TemporaryDirectory() as directory, \
                patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command):
            output = Path(directory)
            KUBERNETES_CAMPAIGN.collect_pod_logs(["kubectl"], "campaign", [pod], output)
            pod["status"]["containerStatuses"][0]["restartCount"] = 1
            KUBERNETES_CAMPAIGN.collect_pod_logs(["kubectl"], "campaign", [pod], output, previous_only=True)
            pod["status"]["containerStatuses"][0]["restartCount"] = 2
            manifest = KUBERNETES_CAMPAIGN.collect_pod_logs(["kubectl"], "campaign", [pod], output)
            self.assertTrue(manifest["complete"])
            previous = [row for row in manifest["logs"] if row["previous"]]
            self.assertEqual({0, 1}, {row["restart_index"] for row in previous})
            self.assertTrue(all("finished previous" in (output / row["path"]).read_text() for row in previous))

    def test_kubernetes_collect_retains_previous_container_logs_with_restart_identity(self):
        namespace = "slimfaas-pr506-log-regression"
        pod = {"metadata": {"name": "slimfaas-0", "uid": "original-uid"},
               "spec": {"containers": [{"name": "runtime"}]},
               "status": {"containerStatuses": [{"name": "runtime", "restartCount": 1}]}}
        commands = []

        def command(arguments, **kwargs):
            if "logs" in arguments:
                commands.append(arguments)
                kwargs["stdout"].write("previous OutOfMemoryException\n" if "--previous" in arguments
                                       else "current healthy process\n")
                return SimpleNamespace(returncode=0)
            resource = arguments[arguments.index("get") + 1]
            result = ({"metadata": {"labels": {"slimfaas-campaign": namespace}}}
                      if resource == "namespace" else {"items": [pod] if resource == "pods" else []})
            return SimpleNamespace(stdout=json.dumps(result), returncode=0)

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            arguments = ["campaign", "collect", "--kubeconfig", "isolated-config",
                         "--namespace", namespace, "--output", str(output)]
            with patch.object(sys, "argv", arguments), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command):
                self.assertEqual(0, KUBERNETES_CAMPAIGN.main())
            self.assertTrue(any("OutOfMemoryException" in path.read_text() for path in output.rglob("*.log")))
            self.assertTrue(any("--previous" in value for value in commands))
            self.assertTrue(all("--timestamps" in value for value in commands))
            manifest = json.loads((output / "pod-log-coverage.json").read_text())
            self.assertTrue(manifest["complete"])
            self.assertEqual({0, 1}, {row["restart_index"] for row in manifest["logs"]})
            self.assertTrue(all(row["pod_uid"] == "original-uid" for row in manifest["logs"]))

    def test_kubernetes_collect_reports_unavailable_previous_logs_as_incomplete_evidence(self):
        namespace = "slimfaas-pr506-log-regression"
        pod = {"metadata": {"name": "slimfaas-0", "uid": "original-uid"},
               "spec": {"containers": [{"name": "runtime"}]},
               "status": {"containerStatuses": [{"name": "runtime", "restartCount": 2}]}}

        def command(arguments, **kwargs):
            if "logs" in arguments:
                previous = "--previous" in arguments
                kwargs["stdout"].write("previous logs unavailable\n" if previous else "current healthy process\n")
                return SimpleNamespace(returncode=1 if previous else 0)
            resource = arguments[arguments.index("get") + 1]
            result = ({"metadata": {"labels": {"slimfaas-campaign": namespace}}}
                      if resource == "namespace" else {"items": [pod] if resource == "pods" else []})
            return SimpleNamespace(stdout=json.dumps(result), returncode=0)

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            arguments = ["campaign", "collect", "--kubeconfig", "isolated-config",
                         "--namespace", namespace, "--output", str(output)]
            with patch.object(sys, "argv", arguments), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command):
                self.assertEqual(0, KUBERNETES_CAMPAIGN.main())
            manifest = json.loads((output / "pod-log-coverage.json").read_text())
            self.assertFalse(manifest["complete"])
            self.assertEqual([0, 1], manifest["missing_instances"][0]["restart_indices"])
            failed = [row for row in manifest["logs"] if row["exit_code"] != 0]
            self.assertEqual(1, len(failed))
            self.assertIn("unavailable", (output / failed[0]["path"]).read_text())

    def test_kubernetes_launch_with_a_maximum_length_namespace_keeps_all_labels_valid(self):
        prefix = "slimfaas-pr506-"
        namespace = prefix + "m" * (63 - len(prefix))
        self.assertEqual(63, len(namespace))
        applied = []

        def command(arguments, **_kwargs):
            if "namespace" in arguments:
                return SimpleNamespace(stdout="")
            self.assertIn("apply", arguments)
            document = json.loads(Path(arguments[arguments.index("-f") + 1]).read_text())
            for resource in document["items"]:
                for value in resource["metadata"].get("labels", {}).values():
                    self.assertLessEqual(len(value), 63, resource["metadata"]["name"])
            applied.extend(document["items"])
            return SimpleNamespace(stdout="")

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "campaign"
            arguments = ["campaign", "launch", "--kubeconfig", "isolated-config",
                         "--namespace", namespace, "--output", str(output), "--image", "runtime",
                         "--suite", "memory"]
            with patch.object(sys, "argv", arguments), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "check_output", return_value="source-sha\n"):
                self.assertEqual(0, KUBERNETES_CAMPAIGN.main())
        volumes = [resource for resource in applied if resource["kind"] == "PersistentVolume"]
        self.assertEqual(3, len(volumes))
        self.assertEqual(3, len({volume["metadata"]["name"] for volume in volumes}))
        self.assertEqual(3, len({volume["spec"]["hostPath"]["path"] for volume in volumes}))
        self.assertEqual({f"state-slimfaas-{index}" for index in range(3)},
                         {volume["spec"]["claimRef"]["name"] for volume in volumes})
        for volume in volumes:
            self.assertEqual(namespace, volume["metadata"]["labels"]["slimfaas-campaign"])
            self.assertEqual(namespace, volume["spec"]["claimRef"]["namespace"])
            self.assertEqual("Retain", volume["spec"]["persistentVolumeReclaimPolicy"])

    def test_kubernetes_rejects_an_oversized_namespace_before_any_api_call(self):
        prefix = "slimfaas-pr506-"
        namespace = prefix + "m" * (64 - len(prefix))
        self.assertEqual(64, len(namespace))
        response = SimpleNamespace(stdout=json.dumps({"metadata": {
            "labels": {"slimfaas-campaign": namespace}}, "items": []}))
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(sys, "argv", ["campaign", "stop", "--kubeconfig", "isolated-config",
                "--namespace", namespace, "--output", directory]), \
                patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", return_value=response) as command, \
                patch.object(sys, "stderr", io.StringIO()) as error:
            with self.assertRaises(SystemExit) as failure:
                KUBERNETES_CAMPAIGN.main()
        self.assertEqual(2, failure.exception.code)
        self.assertIn("63", error.getvalue())
        command.assert_not_called()

    def test_kubernetes_stop_waits_for_terminating_controllers_and_active_jobs(self):
        namespace = "slimfaas-pr506-stop-test"
        terminating = {"metadata": {"name": "slimfaas-0", "uid": "runtime-uid",
            "deletionTimestamp": "2026-11-14T12:00:00Z", "labels": {"slimfaas-campaign": namespace},
            "ownerReferences": [{"kind": "StatefulSet"}]}, "status": {"phase": "Running"}}
        completed = {"metadata": {"name": "campaign-driver", "uid": "driver-uid",
            "labels": {"slimfaas-campaign": namespace}, "ownerReferences": [{"kind": "Job"}]},
            "status": {"phase": "Succeeded"}}
        active_job = {"metadata": {"name": "function-job", "uid": "job-uid",
            "labels": {"slimfaas-campaign": namespace}, "ownerReferences": [{"kind": "Job"}]},
            "status": {"phase": "Running"}}
        foreign = {"metadata": {"name": "unrelated", "uid": "foreign-uid",
            "labels": {"slimfaas-campaign": "different"}, "ownerReferences": [{"kind": "ReplicaSet"}]},
            "status": {"phase": "Running"}}
        snapshots = [[terminating, completed, foreign], [active_job, completed, foreign], [completed, foreign]]
        pod_reads = []

        def command(arguments, **_kwargs):
            if "scale" in arguments:
                return SimpleNamespace(stdout="")
            if "namespace" in arguments:
                return SimpleNamespace(stdout=json.dumps({"metadata": {"labels": {"slimfaas-campaign": namespace}}}))
            self.assertIn("pods", arguments)
            pod_reads.append(arguments)
            return SimpleNamespace(stdout=json.dumps({"items": snapshots.pop(0)}))

        with tempfile.TemporaryDirectory() as directory:
            arguments = ["campaign", "stop", "--kubeconfig", "isolated-config", "--namespace", namespace,
                         "--output", directory]
            with patch.object(sys, "argv", arguments), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command) as invoked, \
                    patch.object(KUBERNETES_CAMPAIGN.time, "sleep"):
                self.assertEqual(0, KUBERNETES_CAMPAIGN.main())
            self.assertEqual(3, len(pod_reads))
            proof = json.loads((Path(directory) / "shutdown.json").read_text())
            self.assertTrue(proof["passed"])
            self.assertEqual([], proof["polls"][-1]["remaining_writers"])
            self.assertEqual(["driver-uid"], proof["polls"][-1]["completed_job_uids"])
            self.assertTrue(all("delete" not in call.args[0] for call in invoked.call_args_list))
            self.assertTrue(all("kind-slimfaas-pr506" in call.args[0] for call in invoked.call_args_list))

    def test_kubernetes_stop_timeout_retains_the_unclosed_writer_identity(self):
        namespace = "slimfaas-pr506-stop-timeout"
        pod = {"metadata": {"name": "slimfaas-1", "uid": "still-writing",
            "labels": {"slimfaas-campaign": namespace}, "ownerReferences": [{"kind": "StatefulSet"}]}}

        def command(arguments, **_kwargs):
            if "scale" in arguments:
                return SimpleNamespace(stdout="")
            if "namespace" in arguments:
                return SimpleNamespace(stdout=json.dumps({"metadata": {"labels": {"slimfaas-campaign": namespace}}}))
            return SimpleNamespace(stdout=json.dumps({"items": [pod]}))

        with tempfile.TemporaryDirectory() as directory:
            arguments = ["campaign", "stop", "--kubeconfig", "isolated-config", "--namespace", namespace,
                         "--output", directory]
            with patch.object(sys, "argv", arguments), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command), \
                    patch.object(KUBERNETES_CAMPAIGN.time, "monotonic", side_effect=[0, 181]), \
                    patch.object(KUBERNETES_CAMPAIGN.time, "sleep"):
                with self.assertRaisesRegex(TimeoutError, "still have writers"):
                    KUBERNETES_CAMPAIGN.main()
            proof = json.loads((Path(directory) / "shutdown.json").read_text())
            self.assertFalse(proof["passed"])
            self.assertEqual("still-writing", proof["polls"][-1]["remaining_writers"][0]["uid"])

    def test_kubernetes_stop_scales_only_the_campaign_label(self):
        namespace = "slimfaas-pr506-stop-scope"

        def command(arguments, **_kwargs):
            if "scale" in arguments:
                return SimpleNamespace(stdout="")
            if "namespace" in arguments:
                return SimpleNamespace(stdout=json.dumps({"metadata": {"labels": {"slimfaas-campaign": namespace}}}))
            return SimpleNamespace(stdout='{"items":[]}')

        with tempfile.TemporaryDirectory() as directory:
            with patch.object(sys, "argv", ["campaign", "stop", "--kubeconfig", "isolated-config",
                    "--namespace", namespace, "--output", directory]), \
                    patch.object(KUBERNETES_CAMPAIGN.subprocess, "run", side_effect=command) as invoked:
                self.assertEqual(0, KUBERNETES_CAMPAIGN.main())
            scales = [call.args[0] for call in invoked.call_args_list if "scale" in call.args[0]]
            self.assertEqual(2, len(scales))
            for arguments in scales:
                self.assertNotIn("--all", arguments)
                self.assertEqual("slimfaas-campaign=" + namespace, arguments[arguments.index("--selector") + 1])

    def test_kubernetes_matrix_refuses_to_archive_after_stop_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            phase = Path(directory) / "http-attempt-1" / "1-before"
            phase.parent.mkdir()
            configuration = {"environment": "kubernetes", "prefix": "slimfaas-pr506-test",
                             "kubeconfig": "isolated-config", "before": "reference-runtime",
                             "before_commit": "reference-source", "controller_image": "common-driver"}

            def command(arguments, _log, check=False, **_kwargs):
                if arguments[2] == "launch":
                    Path(arguments[arguments.index("--output") + 1]).mkdir(parents=True)
                if arguments[2] == "stop":
                    if check:
                        raise subprocess.CalledProcessError(1, arguments)
                    return 1
                return 0

            with patch.object(MATRIX.shutil, "disk_usage", return_value=SimpleNamespace(free=40 * 1024**3)), \
                    patch.object(MATRIX.subprocess, "check_output", return_value=f"Avail\n{40 * 1024**3}\n"), \
                    patch.object(MATRIX, "invoke", side_effect=command), \
                    patch.object(MATRIX, "export_vm") as archive:
                with self.assertRaises(subprocess.CalledProcessError):
                    MATRIX.run_one.__wrapped__(configuration, "http", None, None, "before", 1, phase)
                archive.assert_not_called()

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

    def test_failed_http_execution_is_not_reported_as_body_corruption_or_a_successful_duplicate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "operations.json"
            output.write_text(json.dumps({"report": {"KeyPrefix": "load", "PayloadBytes": 4}, "samples": [
                {"operation": "Async", "Success": True, "Worker": 1, "Sequence": 2}]}))
            receipt = {"event": "completed", "id": "load-async-1-2", "bytes": 4,
                "sha256": hashlib.sha256(b"abcd").hexdigest(), "status": 200}
            (root / "receipts.jsonl").write_text(json.dumps(receipt | {"status": 500}) + "\n" + json.dumps(receipt) + "\n")
            WORKLOAD.verify_receipts(output, root, timeout=0)
            result = json.loads(output.with_suffix(".receipts.json").read_text())
            self.assertTrue(result["passed"])
            self.assertEqual({}, result["duplicates"])
            self.assertEqual({"load-async-1-2": 1}, result["failed_http_attempts"])

    def test_controlled_fault_duplicate_policy_keeps_counts_and_still_requires_delivery_and_hash(self):
        for failure in (None, "missing", "checksum"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                output = root / "operations.json"
                output.write_text(json.dumps({"report": {"KeyPrefix": "load", "PayloadBytes": 4}, "samples": [
                    {"operation": "Async", "Success": True, "Worker": 1, "Sequence": 2}]}))
                receipt = {"event": "completed", "id": "load-async-1-2", "bytes": 4,
                    "sha256": hashlib.sha256(b"abcd").hexdigest(), "status": 200}
                if failure == "checksum":
                    receipt["sha256"] = "corrupt"
                (root / "receipts.jsonl").write_text("" if failure == "missing" else (json.dumps(receipt) + "\n") * 2)
                if failure:
                    with self.assertRaises(AssertionError):
                        WORKLOAD.verify_receipts(output, root, timeout=0, allow_duplicates=True)
                else:
                    WORKLOAD.verify_receipts(output, root, timeout=0, allow_duplicates=True)
                    result = json.loads(output.with_suffix(".receipts.json").read_text())
                    self.assertTrue(result["passed"])
                    self.assertEqual({"load-async-1-2": 2}, result["duplicates"])

    def test_journaled_async_completion_reuses_receipt_observation_with_the_180_second_budget(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "operations.json").write_text(json.dumps({"samples": [
                {"operation": "Async", "Success": True}, {"operation": "Async", "Success": False}]}))
            observer = SimpleNamespace(verify_receipts=Mock())
            phase = {"value": "load"}
            JOURNALED_LOAD.drain_confirmed_async(observer, ["/tools/memorylab/SlimFaas.MemoryLab", "load"], root,
                {"receipts_root": str(root / "receipts")}, phase)
            observer.verify_receipts.assert_called_once_with(root / "operations.json", root / "receipts",
                timeout=180, allow_duplicates=True)
            self.assertEqual("drain", phase["value"])

    def test_journaled_sync_or_unconfirmed_async_outcomes_do_not_create_a_delivery_claim(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "operations.json").write_text(json.dumps({"samples": [
                {"operation": "Sync", "Success": True}, {"operation": "Async", "Success": False}]}))
            observer = SimpleNamespace(verify_receipts=Mock())
            phase = {"value": "load"}
            JOURNALED_LOAD.drain_confirmed_async(observer, ["/tools/memorylab/SlimFaas.MemoryLab", "load"], root,
                {"receipts_root": str(root / "receipts")}, phase)
            observer.verify_receipts.assert_not_called()
            self.assertEqual("load", phase["value"])

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
