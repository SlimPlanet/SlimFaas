#!/usr/bin/env python3
"""Check that routing faults remain confined to owned Kubernetes test resources."""

import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("routing", Path(__file__).with_name("run-kubernetes-routing.py"))
routing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(routing)


class RoutingResourcesTests(unittest.TestCase):
    def build(self, route, replacement_mode="terminating", function_delay_seconds=0, function_controller="deployment", delivery_trace=False):
        return routing.resources(SimpleNamespace(namespace="slimfaas-pr506-routing-test", image="runtime:source",
            controller_image="tools:shared", source_commit="source", route=route, retire_delay=600,
            before_seconds=60, after_seconds=420, concurrency=16, payload_bytes=4096, rps=20, scenario="sync",
            replacement_mode=replacement_mode, function_delay_seconds=function_delay_seconds,
            function_controller=function_controller, delivery_trace=delivery_trace))

    def test_headless_function_dns_omits_unready_and_terminating_addresses(self):
        items, config = self.build("headless")
        service = next(item for item in items if item["kind"] == "Service" and item["metadata"]["name"] == "memory-function")
        self.assertEqual("None", service["spec"]["clusterIP"])
        self.assertFalse(service["spec"]["publishNotReadyAddresses"])
        self.assertEqual("http://{function_name}.{namespace}.svc:5000", config["function_url_template"])

    def test_cluster_ip_service_keeps_a_stable_dns_name(self):
        items, config = self.build("service")
        service = next(item for item in items if item["kind"] == "Service" and item["metadata"]["name"] == "memory-function")
        self.assertNotIn("clusterIP", service["spec"])
        self.assertNotIn("publishNotReadyAddresses", service["spec"])
        self.assertNotIn("{pod_ip}", config["function_url_template"])

    def test_direct_routing_selects_a_pod_instead_of_a_service(self):
        _, config = self.build("direct")
        self.assertEqual("http://{pod_ip}:{pod_port}", config["function_url_template"])

    def test_actual_csharp_generator_runs_in_a_non_retrying_job_with_a_distinct_pod_identity(self):
        items, config = self.build("direct")
        jobs = [item for item in items if item["kind"] == "Job"]
        self.assertEqual(1, len(jobs))
        pod = jobs[0]["spec"]["template"]["spec"]
        self.assertEqual(0, jobs[0]["spec"]["backoffLimit"])
        self.assertEqual("Never", pod["restartPolicy"])
        container = pod["containers"][0]
        self.assertEqual(["python3", "/src/.bin/run-journaled-load.py"], container["command"][:2])
        self.assertEqual("/tools/memorylab/SlimFaas.MemoryLab", container["command"][container["command"].index("--") + 1])
        self.assertIn("--journal-directory", container["command"])
        self.assertEqual("metadata.uid", container["env"][0]["valueFrom"]["fieldRef"]["fieldPath"])
        self.assertEqual("tools:shared", container["image"])
        self.assertEqual(3, len(config["nodes"]))
        backend = next(item for item in items if item["kind"] == "ConfigMap" and item["metadata"]["name"] == "campaign")
        self.assertTrue(json.loads(backend["data"]["backend.json"])["process_socket_statistics"])

    def test_old_function_connections_remain_alive_during_the_observation_window(self):
        items, _ = self.build("direct")
        function = next(item for item in items if item["kind"] == "Deployment" and item["metadata"]["name"] == "memory-function")
        pod = function["spec"]["template"]["spec"]
        self.assertEqual(605, pod["terminationGracePeriodSeconds"])
        self.assertEqual(["sh", "-c", "sleep 600"], pod["containers"][0]["lifecycle"]["preStop"]["exec"]["command"])
        self.assertNotIn("livenessProbe", pod["containers"][0])
        self.assertIn("readinessProbe", pod["containers"][0])

    def test_exit_replacement_ends_the_old_process_instead_of_holding_its_connections(self):
        items, config = self.build("service", replacement_mode="exit", function_delay_seconds=.5)
        function = next(item for item in items if item["kind"] == "Deployment" and item["metadata"]["name"] == "memory-function")
        pod = function["spec"]["template"]["spec"]
        self.assertNotIn("lifecycle", pod["containers"][0])
        self.assertEqual(5, pod["terminationGracePeriodSeconds"])
        self.assertIn({"name": "CHAOS_DELAY_SECONDS", "value": "0.5"}, pod["containers"][0]["env"])
        self.assertEqual("exit", config["replacement_mode"])
        self.assertEqual(.5, config["function_delay_seconds"])

    def test_stopping_a_deleted_uid_never_signals_its_replacement(self):
        namespace = "slimfaas-pr506-routing-test"
        pods = [{"metadata": {"name": "function-" + uid, "uid": uid, "labels": {
            "slimfaas-campaign": namespace, "app": "memory-function"}}, "spec": {"containers": [{"name": "function",
            "image": "tools:shared", "command": ["python3", "/src/tests/SlimFaas.Tests/Local/chaos_function.py"]}]},
            "status": {"containerStatuses": [{"name": "function", "state": {"running": {}}}]}}
            for uid in ("old", "replacement")]
        calls = []

        def kubectl(*args, **_kwargs):
            calls.append(args)
            return SimpleNamespace(stdout=json.dumps({"items": pods}))

        stopped = routing.terminate_owned_functions(kubectl, namespace, "tools:shared", only_uid="old")
        self.assertEqual(["old"], [row["uid"] for row in stopped])
        commands = [call for call in calls if call[0] == "exec"]
        self.assertEqual(1, len(commands))
        self.assertEqual("function-old", commands[0][1])
        self.assertIn("old", commands[0])

    def test_stateful_function_reuses_its_ordinal_after_the_old_process_exits(self):
        items, config = self.build("direct", replacement_mode="exit", function_controller="statefulset")
        function = next(item for item in items if item["metadata"]["name"] == "memory-function" and item["kind"] == "StatefulSet")
        self.assertEqual("memory-function", function["spec"]["serviceName"])
        self.assertEqual(1, function["spec"]["replicas"])
        self.assertEqual("true", function["spec"]["template"]["metadata"]["annotations"]["SlimFaas/Function"])
        self.assertEqual("statefulset", config["function_controller"])
        runtime = next(item for item in items if item["metadata"]["name"] == "slimfaas" and item["kind"] == "StatefulSet")
        environment = runtime["spec"]["template"]["spec"]["containers"][0]["env"]
        self.assertIn({"name": "SlimFaas__BaseFunctionUrl", "value": "http://{pod_ip}:{pod_port}"}, environment)

    def test_stateful_function_rejects_a_terminating_process_that_blocks_ordinal_replacement(self):
        with self.assertRaisesRegex(ValueError, "ordinal"):
            self.build("direct", function_controller="statefulset")

    def test_delivery_trace_is_explicit_and_keeps_the_canonical_load_command(self):
        items, traced = self.build("direct", delivery_trace=True)
        _, standard = self.build("direct")
        runtime = next(item for item in items if item["metadata"]["name"] == "slimfaas" and item["kind"] == "StatefulSet")
        environment = runtime["spec"]["template"]["spec"]["containers"][0]["env"]
        self.assertIn({"name": "Logging__LogLevel__SlimFaas.SlimQueuesWorker", "value": "Trace"}, environment)
        self.assertIn({"name": "Logging__LogLevel__SlimData.SlimPersistentState", "value": "Trace"}, environment)
        self.assertEqual(len(environment), len({value["name"] for value in environment}))
        self.assertTrue(traced["delivery_trace"])
        self.assertFalse(standard["delivery_trace"])
        self.assertEqual(standard["driver_command"], traced["driver_command"])

    def test_a_failed_log_capture_still_stops_owned_resources_and_preserves_the_original_error(self):
        namespace = "slimfaas-pr506-routing-test"
        pods = [{"metadata": {"name": "slimfaas-" + str(index), "uid": str(index), "labels": {"app": "slimfaas"}},
                 "status": {"conditions": [{"type": "Ready", "status": "True"}]}} for index in range(3)]
        pods.append({"metadata": {"name": "function-old", "uid": "old", "labels": {"app": "memory-function", "slimfaas-campaign": namespace}},
            "spec": {"containers": [{"name": "function", "image": "tools:shared", "command": ["python3", "/src/tests/SlimFaas.Tests/Local/chaos_function.py"]}]},
            "status": {"podIP": "10.244.1.5", "conditions": [{"type": "Ready", "status": "True"}]}})

        def command(args, **_kwargs):
            if "get" not in args:
                return SimpleNamespace(stdout="", returncode=0)
            kind = args[args.index("get") + 1]
            if kind == "namespace":
                return SimpleNamespace(stdout="", returncode=0)
            items = ([{"metadata": {"name": "slimfaas-pr506-" + str(i)}} for i in range(4)] if kind == "nodes"
                     else pods if kind == "pods" else [])
            if kind == "pods" and "-l" in args:
                items = [pods[-1]]
            return SimpleNamespace(stdout=json.dumps({"items": items}), returncode=0)

        with tempfile.TemporaryDirectory() as temporary:
            options = SimpleNamespace(namespace=namespace, image="runtime:source", controller_image="tools:shared",
                source_commit="source", route="direct", retire_delay=600, before_seconds=60, after_seconds=420,
                concurrency=1, payload_bytes=4096, rps=20, scenario="sync", replacement_mode="terminating",
                function_delay_seconds=0, function_controller="deployment", delivery_trace=False, output=Path(temporary) / "case",
                kubeconfig=Path(temporary) / "dedicated-kubeconfig", dry_run=False)
            with patch.object(routing.subprocess, "run", side_effect=command) as run, patch.object(
                    routing.campaign, "collect_pod_logs", side_effect=RuntimeError("capture failed")):
                with self.assertRaisesRegex(RuntimeError, "capture failed"):
                    routing.run(options)
            calls = [call.args[0] for call in run.call_args_list]
            self.assertTrue(any("scale" in call and "--replicas=0" in call and namespace in call for call in calls))
            self.assertTrue(any("delete" in call and namespace in call for call in calls))
            report = json.loads((options.output / "execution.json").read_text())
            self.assertIn("capture failed", report["error"])
            self.assertTrue(report["cleanup_errors"])

    def test_function_shutdown_refuses_an_unowned_or_different_process(self):
        namespace = "slimfaas-pr506-routing-test"
        pod = {"metadata": {"name": "function", "uid": "original", "labels": {
            "slimfaas-campaign": "another-campaign", "app": "memory-function"}},
            "spec": {"containers": [{"name": "function", "image": "tools:shared", "command": ["python3", "another.py"]}]}}
        with patch.object(routing.subprocess, "run"):
            calls = []

            def kubectl(*args, **_kwargs):
                calls.append(args)
                return SimpleNamespace(stdout=json.dumps({"items": [pod]}))

            with self.assertRaisesRegex(ValueError, "unowned"):
                routing.terminate_owned_functions(kubectl, namespace, "tools:shared")
            self.assertFalse(any("exec" in call for call in calls))
            pod["metadata"]["labels"]["slimfaas-campaign"] = namespace
            with self.assertRaisesRegex(ValueError, "different function"):
                routing.terminate_owned_functions(kubectl, namespace, "tools:shared")
            self.assertFalse(any("exec" in call for call in calls))


if __name__ == "__main__":
    unittest.main(verbosity=2)
