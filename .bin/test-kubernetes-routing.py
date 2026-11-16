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
    def build(self, route):
        return routing.resources(SimpleNamespace(namespace="slimfaas-pr506-routing-test", image="runtime:source",
            controller_image="tools:shared", source_commit="source", route=route, retire_delay=600,
            before_seconds=60, after_seconds=420, concurrency=16, payload_bytes=4096, rps=20, scenario="sync"))

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

    def test_old_function_connections_remain_alive_during_the_observation_window(self):
        items, _ = self.build("direct")
        function = next(item for item in items if item["kind"] == "Deployment" and item["metadata"]["name"] == "memory-function")
        pod = function["spec"]["template"]["spec"]
        self.assertEqual(605, pod["terminationGracePeriodSeconds"])
        self.assertEqual(["sh", "-c", "sleep 600"], pod["containers"][0]["lifecycle"]["preStop"]["exec"]["command"])
        self.assertNotIn("livenessProbe", pod["containers"][0])
        self.assertIn("readinessProbe", pod["containers"][0])

    def test_a_failed_log_capture_still_stops_owned_resources_and_preserves_the_original_error(self):
        namespace = "slimfaas-pr506-routing-test"
        pods = [{"metadata": {"name": "slimfaas-" + str(index), "uid": str(index), "labels": {"app": "slimfaas"}},
                 "status": {"conditions": [{"type": "Ready", "status": "True"}]}} for index in range(3)]
        pods.append({"metadata": {"name": "function-old", "uid": "old", "labels": {"app": "memory-function"}},
            "status": {"podIP": "10.244.1.5", "conditions": [{"type": "Ready", "status": "True"}]}})

        def command(args, **_kwargs):
            if "get" not in args:
                return SimpleNamespace(stdout="", returncode=0)
            kind = args[args.index("get") + 1]
            if kind == "namespace":
                return SimpleNamespace(stdout="", returncode=0)
            items = ([{"metadata": {"name": "slimfaas-pr506-" + str(i)}} for i in range(4)] if kind == "nodes"
                     else pods if kind == "pods" else [])
            return SimpleNamespace(stdout=json.dumps({"items": items}), returncode=0)

        with tempfile.TemporaryDirectory() as temporary:
            options = SimpleNamespace(namespace=namespace, image="runtime:source", controller_image="tools:shared",
                source_commit="source", route="direct", retire_delay=600, before_seconds=60, after_seconds=420,
                concurrency=1, payload_bytes=4096, rps=20, scenario="sync", output=Path(temporary) / "case",
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
