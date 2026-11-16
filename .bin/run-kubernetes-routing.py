#!/usr/bin/env python3
"""Replace an owned function pod under the canonical C# load generator in kind."""

import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import time
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("campaign", Path(__file__).with_name("run-kubernetes-campaign.py"))
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)


def resources(options):
    base = SimpleNamespace(namespace=options.namespace, image=options.image,
        controller_image=options.controller_image, source_commit=options.source_commit,
        suite="memory", probes="normal", variant="routing", ordinal=1, workload_args=[])
    items, config = campaign.resources(base)
    items = [item for item in items if item["kind"] != "Job"]
    function = next(item for item in items if item["kind"] == "Deployment" and item["metadata"]["name"] == "memory-function")
    pod = function["spec"]["template"]["spec"]
    pod["terminationGracePeriodSeconds"] = options.retire_delay + 5
    container = pod["containers"][0]
    container["lifecycle"] = {"preStop": {"exec": {"command": ["sh", "-c", f"sleep {options.retire_delay}"]}}}
    container["env"].append({"name": "POD_IP", "valueFrom": {"fieldRef": {"fieldPath": "status.podIP"}}})
    function["spec"]["template"]["metadata"]["annotations"]["SlimFaas/Configuration"] = json.dumps({
        "DefaultSync": {"HttpTimeout": 15}})
    service = next(item for item in items if item["kind"] == "Service" and item["metadata"]["name"] == "memory-function")
    if options.route == "headless":
        # Raft's service deliberately publishes unready addresses; a function's
        # DNS test must instead exercise the normal ready EndpointSlice policy.
        service["spec"]["clusterIP"] = "None"
        service["spec"]["publishNotReadyAddresses"] = False
    runtime = next(item for item in items if item["kind"] == "StatefulSet")
    environment = runtime["spec"]["template"]["spec"]["containers"][0]["env"]
    target = "http://{pod_ip}:{pod_port}" if options.route == "direct" else "http://{function_name}.{namespace}.svc:5000"
    environment += [{"name": "SlimFaas__BaseFunctionUrl", "value": target},
        {"name": "Logging__LogLevel__SlimFaas.SendClient", "value": "Debug"},
        {"name": "Logging__Console__FormatterOptions__TimestampFormat", "value": "O"},
        {"name": "Logging__Console__FormatterOptions__UseUtcTimestamp", "value": "true"}]
    directory = "/evidence/runs/" + options.namespace + "/routing"
    labels = {"slimfaas-campaign": options.namespace, "app": "routing-driver"}
    command = ["/tools/memorylab/SlimFaas.MemoryLab", "load", "--scenario", options.scenario,
        "--duration", str(options.before_seconds + options.after_seconds),
        "--concurrency", str(options.concurrency), "--payload-bytes", str(options.payload_bytes),
        "--target-rps", str(options.rps), "--function", "memory-function", "--node-urls",
        ",".join(node["http"] for node in config["nodes"]), "--key-prefix", options.namespace,
        "--journal-directory", directory + "/requests", "--csv", directory + "/operations.csv",
        "--json", directory + "/operations.json"]
    job_command = ["python3", "/src/.bin/run-journaled-load.py", "--backend-config", "/config/backend.json",
        "--output", directory, "--"] + command
    items.append({"apiVersion": "batch/v1", "kind": "Job", "metadata": {
        "name": "routing-driver", "namespace": options.namespace, "labels": labels}, "spec": {
        "backoffLimit": 0, "activeDeadlineSeconds": options.before_seconds + options.after_seconds + 300,
        "template": {"metadata": {"labels": labels}, "spec": {
            "restartPolicy": "Never", "nodeSelector": {"node-role.kubernetes.io/control-plane": ""},
            "tolerations": [{"key": "node-role.kubernetes.io/control-plane", "effect": "NoSchedule"}],
            "volumes": [{"name": "evidence", "hostPath": {"path": "/evidence", "type": "Directory"}},
                {"name": "config", "configMap": {"name": "campaign"}},
                {"name": "tools-source", "configMap": {"name": "tools-source"}}],
            "containers": [{"name": "driver", "image": options.controller_image, "imagePullPolicy": "IfNotPresent",
                "command": job_command, "env": [{"name": "POD_UID", "valueFrom": {"fieldRef": {"fieldPath": "metadata.uid"}}}],
                "volumeMounts": [{"name": "evidence", "mountPath": "/evidence"},
                    {"name": "config", "mountPath": "/config", "readOnly": True}],
                "resources": {"requests": {"cpu": "100m", "memory": "128Mi"}, "limits": {"memory": "1Gi"}}}]}}}})
    source = next(item for item in items if item["kind"] == "ConfigMap" and item["metadata"]["name"] == "tools-source")
    source["data"]["run-journaled-load.py"] = Path(__file__).with_name("run-journaled-load.py").read_text()
    driver = items[-1]["spec"]["template"]["spec"]["containers"][0]
    for name in source["data"]:
        driver["volumeMounts"].append({"name": "tools-source", "mountPath": "/src/.bin/" + name,
            "subPath": name, "readOnly": True})
    return items, config | {"route": options.route, "function_url_template": target,
        "routing_directory": directory, "driver_command": command, "retire_delay": options.retire_delay}


def run(options):
    if not re.fullmatch(r"slimfaas-pr506-routing-[a-z0-9-]+", options.namespace) or len(options.namespace) > 63:
        raise ValueError("An isolated slimfaas-pr506-routing-* namespace is required")
    if options.before_seconds < 10 or options.after_seconds < 15 or options.retire_delay <= options.after_seconds:
        raise ValueError("Require at least 10s before, 15s after, and retirement after the observation window")
    if options.concurrency < 1 or options.rps <= 0 or not 0 <= options.payload_bytes <= 1048576:
        raise ValueError("Invalid load parameters")
    options.output.mkdir(parents=True, exist_ok=False)
    items, config = resources(options)
    manifest = options.output / "resources.json"
    manifest.write_text(json.dumps({"apiVersion": "v1", "kind": "List", "items": items}, indent=2) + "\n")
    (options.output / "configuration.json").write_text(json.dumps(config, indent=2) + "\n")
    if options.dry_run:
        return
    prefix = ["kubectl", "--kubeconfig", str(options.kubeconfig.resolve()), "--context", "kind-slimfaas-pr506"]

    def kubectl(*args, **kwargs):
        return subprocess.run(prefix + list(args), text=True, check=True, timeout=40, **kwargs)

    def get(*args):
        return json.loads(kubectl("get", *args, "-o", "json", capture_output=True).stdout)

    nodes = get("nodes")["items"]
    if len(nodes) != 4 or any(not node["metadata"]["name"].startswith("slimfaas-pr506-") for node in nodes):
        raise ValueError("Refusing to mutate a cluster other than the four dedicated kind nodes")
    existing = kubectl("get", "namespace", options.namespace, "--ignore-not-found", "-o", "json", capture_output=True).stdout
    if existing.strip():
        raise ValueError("A routing namespace cannot be reused")
    initial = options.output / "initial-resources.json"
    initial.write_text(json.dumps({"apiVersion": "v1", "kind": "List", "items": [item for item in items if item["kind"] != "Job"]}))
    started = time.time()
    with (options.output / "apply.log").open("w") as log:
        kubectl("apply", "-f", str(initial), stdout=log, stderr=subprocess.STDOUT)
    report = {"started_utc": started, "source": options.source_commit, "config": config, "complete": False}
    timeline = (options.output / "observations.jsonl").open("x", buffering=1)

    def observe():
        value = {"utc": time.time(), "monotonic": time.monotonic(),
            "pods": get("pods", "-n", options.namespace)["items"],
            "endpoints": get("endpointslices.discovery.k8s.io", "-n", options.namespace)["items"]}
        timeline.write(json.dumps(value) + "\n")
        return value

    def ready(pod):
        return not pod["metadata"].get("deletionTimestamp") and any(
            condition["type"] == "Ready" and condition["status"] == "True" for condition in pod.get("status", {}).get("conditions", []))

    def collect_logs():
        pods = get("pods", "-n", options.namespace)["items"]
        return campaign.collect_pod_logs(prefix, options.namespace, pods, options.output)

    driver_created = False
    try:
        deadline = time.monotonic() + 300
        while True:
            observation = observe()
            runtime = [pod for pod in observation["pods"] if pod["metadata"]["labels"].get("app") == "slimfaas"]
            functions = [pod for pod in observation["pods"] if pod["metadata"]["labels"].get("app") == "memory-function" and ready(pod)]
            if len(runtime) == 3 and all(ready(pod) for pod in runtime) and len(functions) == 1:
                old = functions[0]
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("The routing fixture did not become ready within 300 seconds")
            time.sleep(1)
        collect_logs()
        job_file = options.output / "driver-job.json"
        job_file.write_text(json.dumps(next(item for item in items if item["kind"] == "Job")))
        kubectl("apply", "-f", str(job_file), capture_output=True)
        driver_created = True
        load_started = None
        replaced = False
        deadline = time.monotonic() + options.before_seconds + options.after_seconds + 120
        while True:
            observation = observe()
            job = get("job", "routing-driver", "-n", options.namespace)
            status = job.get("status", {})
            if status.get("startTime") and load_started is None:
                load_started = time.monotonic()
                report["job_start_time"] = status["startTime"]
            if load_started is not None and not replaced and time.monotonic() - load_started >= options.before_seconds:
                current = get("pod", old["metadata"]["name"], "-n", options.namespace)
                if current["metadata"]["uid"] != old["metadata"]["uid"]:
                    raise RuntimeError("Function identity changed before the requested replacement")
                report["deleted_pod"] = {"uid": old["metadata"]["uid"], "ip": old["status"].get("podIP"), "name": old["metadata"]["name"]}
                report["deletion_requested_utc"] = time.time()
                kubectl("delete", "pod", old["metadata"]["name"], "-n", options.namespace, "--wait=false", capture_output=True)
                replaced = True
            replacements = [pod for pod in observation["pods"] if pod["metadata"]["labels"].get("app") == "memory-function" and
                pod["metadata"]["uid"] != old["metadata"]["uid"] and ready(pod)]
            if replaced and replacements and "replacement_ready_utc" not in report:
                report.update(replacement_ready_utc=observation["utc"], replacement_pod={
                    "uid": replacements[0]["metadata"]["uid"], "ip": replacements[0]["status"].get("podIP")})
            if any(condition.get("status") == "True" and condition["type"] in ("Complete", "Failed") for condition in status.get("conditions", [])):
                report.update(complete=True, driver_success=status.get("succeeded", 0) == 1, job=job, finished_utc=time.time())
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("The C# routing load job exceeded its bounded observation window")
            time.sleep(1)
    except BaseException as error:
        report.update(error=repr(error), finished_utc=time.time())
        raise
    finally:
        timeline.close()
        # Collection precedes shutdown; original volumes and receipt journals survive.
        # A broken collector must not prevent shutdown or hide the first error.
        actions = [("logs", collect_logs), ("events", lambda:
            (options.output / "events.json").write_text(json.dumps(get("events", "-n", options.namespace), indent=2) + "\n"))]
        if driver_created and not report["complete"]:
            actions.append(("suspend-incomplete-driver", lambda: kubectl("patch", "job", "routing-driver", "-n", options.namespace,
                "--type=merge", "-p", json.dumps({"spec": {"suspend": True}}), capture_output=True)))
        actions += [("stop-owned-resources", lambda: kubectl("scale", "statefulset,deployments", "-n", options.namespace,
            "-l", "slimfaas-campaign=" + options.namespace, "--replicas=0", capture_output=True)),
            ("finish-owned-function-termination", lambda: kubectl("delete", "pods", "-n", options.namespace,
                "-l", "slimfaas-campaign=" + options.namespace + ",app=memory-function", "--grace-period=1",
                "--wait=false", capture_output=True))]
        cleanup_errors = []
        for action, callback in actions:
            try:
                callback()
            except Exception as error:
                cleanup_errors.append({"action": action, "error": repr(error)})
        report["cleanup_errors"] = cleanup_errors
        (options.output / "execution.json").write_text(json.dumps(report, indent=2) + "\n")
        if cleanup_errors and "error" not in report:
            raise RuntimeError("Routing evidence collection or shutdown failed: " + repr(cleanup_errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--kubeconfig", type=Path, required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--controller-image", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--route", choices=("direct", "service", "headless"), required=True)
    parser.add_argument("--scenario", choices=("sync", "async", "mixed"), default="sync")
    parser.add_argument("--concurrency", type=int, default=1)
    parser.add_argument("--payload-bytes", type=int, default=4096)
    parser.add_argument("--rps", type=float, default=20)
    parser.add_argument("--before-seconds", type=int, default=60)
    parser.add_argument("--after-seconds", type=int, default=420)
    parser.add_argument("--retire-delay", type=int, default=600)
    parser.add_argument("--dry-run", action="store_true")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
