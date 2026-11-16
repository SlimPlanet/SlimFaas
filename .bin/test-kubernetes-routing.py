#!/usr/bin/env python3
"""Check that routing faults remain confined to owned Kubernetes test resources."""

import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest


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


if __name__ == "__main__":
    unittest.main(verbosity=2)
