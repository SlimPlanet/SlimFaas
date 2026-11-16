#!/usr/bin/env python3
"""Deploy, observe and retain an isolated kind campaign using its explicit kubeconfig."""

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[1]


def resources(options):
    ns = options.namespace
    labels = {"slimfaas-campaign": ns}
    base = "/evidence/runs/" + ns
    receipts = base + "/receipts"

    def resource(kind, name, spec=None, **extra):
        versions = {"StatefulSet": "apps/v1", "Deployment": "apps/v1", "Job": "batch/v1",
                    "Role": "rbac.authorization.k8s.io/v1", "RoleBinding": "rbac.authorization.k8s.io/v1"}
        metadata = {"name": name, "labels": labels.copy()}
        # PV names may exceed the 63-character label limit and have no app selector.
        if kind != "PersistentVolume":
            metadata["labels"]["app"] = name
        if kind not in ("Namespace", "PersistentVolume"):
            metadata["namespace"] = ns
        result = {"apiVersion": versions.get(kind, "v1"), "kind": kind, "metadata": metadata} | extra
        if spec is not None:
            result["spec"] = spec
        return result

    def evidence_volume():
        return {"name": "evidence", "hostPath": {"path": "/evidence", "type": "Directory"}}

    def service(name, ports, headless=False):
        value = {"selector": labels | {"app": name}, "ports": [
            {"name": f"p{port}", "port": port, "targetPort": port} for port in ports]}
        if headless:
            value.update({"clusterIP": "None", "publishNotReadyAddresses": True})
        return resource("Service", name, value)

    def deployment(name, command, args, annotations=None, replicas=1, control_plane=False, env=None):
        container = {"name": "function", "image": options.controller_image, "imagePullPolicy": "IfNotPresent",
            "command": command, "args": args, "env": [{"name": "PORT", "value": "5000"},
                {"name": "POD_UID", "valueFrom": {"fieldRef": {"fieldPath": "metadata.uid"}}}] + (env or []),
            "ports": [{"containerPort": 5000}], "volumeMounts": [{"name": "evidence", "mountPath": "/evidence"}],
            "resources": {"requests": {"cpu": "10m", "memory": "64Mi"}},
            "readinessProbe": {"httpGet": {"path": "/health", "port": 5000}, "periodSeconds": 1}}
        pod = {"containers": [container], "volumes": [evidence_volume()], "terminationGracePeriodSeconds": 5}
        if control_plane:
            pod.update({"nodeSelector": {"node-role.kubernetes.io/control-plane": ""},
                        "tolerations": [{"key": "node-role.kubernetes.io/control-plane", "effect": "NoSchedule"}]})
        return resource("Deployment", name, {"replicas": replicas, "selector": {"matchLabels": labels | {"app": name}},
            "template": {"metadata": {"labels": labels | {"app": name}, "annotations": annotations or {}}, "spec": pod}})

    result = [resource("Namespace", ns), resource("ServiceAccount", "slimfaas")]
    result.append(resource("Role", "campaign", rules=[
        {"apiGroups": [""], "resources": ["pods", "pods/log", "services", "endpoints", "events"], "verbs": ["get", "list", "watch"]},
        {"apiGroups": ["apps"], "resources": ["deployments", "statefulsets", "replicasets", "deployments/scale", "statefulsets/scale"],
         "verbs": ["get", "list", "watch", "create", "update", "patch", "delete"]},
        {"apiGroups": ["batch"], "resources": ["jobs", "cronjobs"], "verbs": ["get", "list", "watch", "create", "update", "patch", "delete"]}]))
    result.append(resource("RoleBinding", "campaign", subjects=[{"kind": "ServiceAccount", "name": "slimfaas", "namespace": ns}],
                           roleRef={"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "campaign"}))
    for index in range(3):
        result.append(resource("PersistentVolume", f"{ns}-state-{index}", {
            "capacity": {"storage": "2Gi"}, "accessModes": ["ReadWriteOnce"], "storageClassName": "",
            "persistentVolumeReclaimPolicy": "Retain", "claimRef": {"namespace": ns, "name": f"state-slimfaas-{index}"},
            "hostPath": {"path": f"{base}/state/data/slimfaas-{index}", "type": "DirectoryOrCreate"}}))

    job_configuration = {"Configurations": {"chaos-job": {
        "Image": options.controller_image, "ImagesWhitelist": [], "Visibility": "Public",
        "NumberParallelJob": 2, "BackoffLimit": 1, "TtlSecondsAfterFinished": 3600, "RestartPolicy": "Never",
        "Resources": {"Requests": {"cpu": "100m", "memory": "100Mi"},
                      "Limits": {"cpu": "1000m", "memory": "256Mi"}},
        "Environments": [{"Name": "SLIMFAAS_VALIDATION_JOB", "Value": "1"},
                         {"Name": "POD_UID", "FieldRef": {"FieldPath": "metadata.uid"}},
                         {"Name": "CHAOS_RECEIPT_URL", "Value": "http://receipts:8081/receipts"}]}}}
    environment = [{"name": "SlimFaas__BaseSlimDataUrl", "value": "http://{pod_name}.slimfaas.{namespace}.svc:3262"},
        {"name": "SlimData__Directory", "value": "/database"},
        {"name": "SlimData__AllowColdStart", "value": "true"},
        {"name": "SlimData__BackupDirectory", "value": "/database/backup"},
        {"name": "SlimFaas__Namespace", "value": ns},
        {"name": "SlimFaas__JobsConfiguration", "value": json.dumps(job_configuration)},
        {"name": "Data__DefaultVisibility", "value": "Public"},
        {"name": "OpenTelemetry__Enable", "value": "false"},
        {"name": "Logging__LogLevel__Default", "value": "Warning"},
        {"name": "Logging__LogLevel__Microsoft.AspNetCore", "value": "Error"},
        {"name": "SlimFaas__DeadLetterQueue__EnableFront", "value": "true"},
        {"name": "SlimFaas__DeadLetterQueue__DefaultVisibility", "value": "Public"},
        {"name": "SlimFaas__Streams__EnableFront", "value": "true"},
        {"name": "SlimFaas__Streams__DefaultVisibility", "value": "Public"}]
    if options.suite != "http":
        environment += [{"name": "SlimData__SnapshotIntervalEntries", "value": "50"},
                        {"name": "SlimData__SnapshotIntervalBytes", "value": "8388608"}]
    probe = {"httpGet": {"path": "/ready", "port": 5000}, "periodSeconds": 2, "timeoutSeconds": 2}
    runtime = {"name": "runtime", "image": options.image, "imagePullPolicy": "IfNotPresent", "env": environment,
        "securityContext": {"runAsUser": 1000, "runAsGroup": 1000},
        "volumeMounts": [{"name": "state", "mountPath": "/database"}],
        "ports": [{"containerPort": port} for port in (5000, 5003, 3262)], "readinessProbe": probe,
        "resources": {"requests": {"cpu": "250m", "memory": "256Mi"}, "limits": {"memory": "1Gi"}}}
    if options.probes == "normal":
        runtime["startupProbe"] = {"httpGet": {"path": "/health", "port": 5000},
                                   "periodSeconds": 2, "timeoutSeconds": 2, "failureThreshold": 150}
        runtime["livenessProbe"] = {"httpGet": {"path": "/health", "port": 5000},
                                    "initialDelaySeconds": 10, "periodSeconds": 10, "timeoutSeconds": 8}
    init = {"name": "initialize-owned-storage", "image": options.controller_image, "imagePullPolicy": "IfNotPresent",
        "command": ["sh", "-c"], "args": [f"mkdir -p /database/backup {receipts} && chmod 0777 /database /database/backup {receipts}"],
        "volumeMounts": [{"name": "state", "mountPath": "/database"}, {"name": "evidence", "mountPath": "/evidence"}]}
    agent = {"name": "fault-agent", "image": options.controller_image, "imagePullPolicy": "IfNotPresent",
        "securityContext": {"capabilities": {"add": ["SYS_PTRACE"]}},
        "command": ["python3", "/src/.bin/slimdata_fault_agent.py"],
        "env": [{"name": "POD_UID", "valueFrom": {"fieldRef": {"fieldPath": "metadata.uid"}}}],
        "ports": [{"containerPort": 39777}], "resources": {"requests": {"cpu": "5m", "memory": "32Mi"}}}
    if options.suite != "chaos":
        # The historical runtime bound every declared sidecar port. Keep the
        # performance fixture identical and executable for both revisions; the
        # chaos fixture above independently exercises declared sidecar ports.
        agent.pop("ports")
    pod = {"serviceAccountName": "slimfaas", "shareProcessNamespace": True,
        "containers": [runtime, agent], "initContainers": [init], "volumes": [evidence_volume()],
        "terminationGracePeriodSeconds": 5,
        "affinity": {"podAntiAffinity": {"requiredDuringSchedulingIgnoredDuringExecution": [{
            "labelSelector": {"matchLabels": labels | {"app": "slimfaas"}}, "topologyKey": "kubernetes.io/hostname"}]}}}
    result.append(resource("StatefulSet", "slimfaas", {"serviceName": "slimfaas", "replicas": 3,
        "podManagementPolicy": "Parallel", "selector": {"matchLabels": labels | {"app": "slimfaas"}},
        "template": {"metadata": {"labels": labels | {"app": "slimfaas"}}, "spec": pod},
        "volumeClaimTemplates": [{"metadata": {"name": "state"}, "spec": {"storageClassName": "",
            "accessModes": ["ReadWriteOnce"], "resources": {"requests": {"storage": "2Gi"}}}}]}))
    result.append(service("slimfaas", (5000, 5003, 3262, 39777), headless=True))
    entry_service = service("slimfaas-http", (5000, 5003))
    entry_service["spec"]["selector"]["app"] = "slimfaas"
    result.append(entry_service)

    common = {"SlimFaas/Function": "true", "SlimFaas/ReplicasAtStart": "1", "SlimFaas/ReplicasMin": "1",
              "SlimFaas/NumberParallelRequest": "256", "SlimFaas/NumberParallelRequestPerPod": "256"}
    fixture = "/src/tests/SlimFaas.Tests/Local/chaos_function.py"
    if options.suite == "http":
        result.append(deployment("benchmark-latency", ["/tools/benchmark/SlimFaas.Benchmarks"],
                                 ["target", "--port", "5000", "--host", "0.0.0.0"], common))
        scale = {"ReplicaMax": 4, "ScrapeIntervalMilliseconds": 1000,
            "Triggers": [{"MetricType": "AverageValue", "MetricName": "benchmark_scale_pressure",
                          "Query": "max_over_time(slimfaas_benchmark_scale_pressure[30s])", "Threshold": 1}],
            "Behavior": {"ScaleUp": {"StabilizationWindowSeconds": 0, "Policies": []},
                         "ScaleDown": {"StabilizationWindowSeconds": 0, "Policies": []}}}
        result.append(deployment("benchmark-scale", ["/tools/benchmark/SlimFaas.Benchmarks"],
            ["target", "--port", "5000", "--host", "0.0.0.0"], common | {
                "SlimFaas/ReplicasMin": "0", "SlimFaas/TimeoutSecondBeforeSetReplicasMin": "5",
                "SlimFaas/NumberParallelRequest": "4", "SlimFaas/NumberParallelRequestPerPod": "1",
                "prometheus.io/path": "/metrics", "prometheus.io/port": "5000", "prometheus.io/scrape": "true",
                "SlimFaas/Scale": json.dumps(scale)}))
        result.append(service("benchmark-latency", (5000,)))
        direct = "http://benchmark-latency:5000"
    else:
        annotations = common | {"SlimFaas/NumberParallelRequest": "64", "SlimFaas/NumberParallelRequestPerPod": "64"}
        names = ["memory-function"] if options.suite == "memory" else ["chaos", "chaos-wake", "chaos-fanout-a", "chaos-fanout-b"]
        for name in names:
            specific = annotations.copy()
            if name == "chaos":
                specific |= {"SlimFaas/SubscribeStream": "Public:chaos-stream", "SlimFaas/StreamMaxLength": "100000",
                             "SlimFaas/StreamMaxAge": "24h"}
            if name == "chaos-wake":
                specific |= {"SlimFaas/ReplicasMin": "0", "SlimFaas/TimeoutSecondBeforeSetReplicasMin": "2"}
            if name.startswith("chaos-fanout"):
                specific |= {"SlimFaas/SubscribeStream": "Public:chaos-bounded", "SlimFaas/StreamMaxLength": "3",
                             "SlimFaas/StreamMaxAge": "15s"}
            if name.startswith("chaos"):
                specific["SlimFaas/Configuration"] = json.dumps({"DefaultAsync": {"HttpTimeout": 8,
                    "TimeoutRetries": [1, 2], "HttpStatusRetries": [500, 502, 503, 504],
                    "DeadLetterQueue": {"Enabled": True, "MaxAgeSeconds": 604800, "MaxLength": 100000}}})
            result.append(deployment(name, ["python3", fixture], ["--host", "0.0.0.0", "--journal-directory", receipts], specific,
                                     env=[{"name": "CHAOS_FUNCTION", "value": name}]))
        result.append(service(names[0], (5000,)))
        direct = "http://" + names[0] + ":5000"
    sink = deployment("receipts", ["python3", "/src/.bin/slimdata_receipt_sink.py"],
                      ["--directory", receipts], control_plane=True)
    sink["spec"]["template"]["spec"]["containers"][0]["readinessProbe"]["httpGet"]["port"] = 8081
    result.extend([sink, service("receipts", (8081,))])
    config = {"namespace": ns, "campaign_id": ns, "entry": "http://slimfaas-http:5000", "direct": direct,
        "runtime_image": options.image, "controller_image": options.controller_image,
        "source_commit": options.source_commit,
        "state_root": base + "/state", "receipts_root": receipts,
        "node_state_roots": [f"{base}/state/data/slimfaas-{index}/slimfaas-{index}" for index in range(3)],
        "nodes": [{kind: f"{scheme}://slimfaas-{index}.slimfaas:{port}" + ("/ws" if kind == "websocket" else "")
                   for kind, scheme, port in (("http", "http", 5000), ("raft", "http", 3262),
                                              ("control", "http", 39777), ("websocket", "ws", 5003))} for index in range(3)]}
    result.append(resource("ConfigMap", "campaign", data={"backend.json": json.dumps(config)}))
    if options.suite == "chaos":
        command = ["python3", "/src/.bin/test-slimdata-local-chaos.py", "--runtime", "/app/SlimFaas",
            "--backend-config", "/config/backend.json", "--output", base + "/campaign",
            "--state-directory", base + "/state", "--scenarios", options.scenarios,
            "--roles", options.roles, "--durations", options.durations, "--recovery-timeout", "300",
            "--traffic-interval", "2", "--source-commit", options.source_commit]
        command += options.workload_args
    else:
        command = ["python3", "/src/.bin/run-platform-workload.py", "--suite", options.suite,
            "--runtime", "/app/SlimFaas", "--benchmark", "/tools/benchmark/SlimFaas.Benchmarks",
            "--memorylab", "/tools/memorylab/SlimFaas.MemoryLab", "--backend-config", "/config/backend.json",
            "--output", base + "/workload", "--variant", options.variant, "--ordinal", str(options.ordinal)] + options.workload_args
    driver = {"serviceAccountName": "slimfaas", "restartPolicy": "Never",
        "nodeSelector": {"node-role.kubernetes.io/control-plane": ""},
        "tolerations": [{"key": "node-role.kubernetes.io/control-plane", "effect": "NoSchedule"}],
        "containers": [{"name": "driver", "image": options.controller_image, "imagePullPolicy": "IfNotPresent",
            "command": command, "volumeMounts": [{"name": "evidence", "mountPath": "/evidence"},
                {"name": "config", "mountPath": "/config", "readOnly": True}],
            "resources": {"requests": {"cpu": "100m", "memory": "128Mi"}, "limits": {"memory": "2Gi"}}}],
        "volumes": [evidence_volume(), {"name": "config", "configMap": {"name": "campaign"}}]}
    # The controller waits for runtime initialization. Its output directory must
    # not be pre-created: the ledger deliberately rejects accidental run reuse.
    driver["initContainers"] = [{"name": "wait-for-owned-storage", "image": options.controller_image,
        "imagePullPolicy": "IfNotPresent", "command": ["sh", "-c"],
        "args": [f"until test -d {receipts}; do sleep 1; done"],
        "volumeMounts": [{"name": "evidence", "mountPath": "/evidence"}]}]
    result.append(resource("Job", "campaign-driver", {"backoffLimit": 0,
        "template": {"metadata": {"labels": labels | {"app": "campaign-driver"}}, "spec": driver}}))
    sources = {name: (ROOT / ".bin" / name).read_text() for name in (
        "test-slimdata-local-chaos.py", "slimdata_kubernetes_backend.py", "slimdata_fault_agent.py",
        "slimdata_receipt_sink.py", "run-platform-workload.py")}
    sources["chaos_function.py"] = (ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py").read_text()
    result.append(resource("ConfigMap", "tools-source", data=sources, immutable=True))
    for item in result:
        if item["kind"] not in ("Deployment", "StatefulSet", "Job"):
            continue
        pod_spec = item["spec"]["template"]["spec"]
        pod_spec.setdefault("volumes", []).append({"name": "tools-source", "configMap": {"name": "tools-source"}})
        for container in pod_spec["containers"]:
            for name in sources:
                target = "/src/tests/SlimFaas.Tests/Local/chaos_function.py" if name == "chaos_function.py" else "/src/.bin/" + name
                container.setdefault("volumeMounts", []).append({"name": "tools-source", "mountPath": target,
                                                                 "subPath": name, "readOnly": True})
    return result, config


def wait_for_campaign_quiescence(kubectl, namespace, output, timeout=180):
    """Keep final WAL flushes and receipt writers out of evidence archiving."""
    output.mkdir(parents=True, exist_ok=True)
    proof = {"namespace": namespace, "started_utc": time.time(), "passed": False, "polls": []}
    deadline = time.monotonic() + timeout
    while True:
        pods = json.loads(kubectl("get", "pods", "-n", namespace, "--selector",
            "slimfaas-campaign=" + namespace, "-o", "json", capture_output=True).stdout)["items"]
        remaining = []
        completed_jobs = []
        for pod in pods:
            metadata = pod["metadata"]
            if metadata.get("labels", {}).get("slimfaas-campaign") != namespace:
                continue
            owners = {owner["kind"] for owner in metadata.get("ownerReferences", [])}
            completed = pod.get("status", {}).get("phase") in ("Succeeded", "Failed")
            if owners.intersection(("StatefulSet", "ReplicaSet")) or not completed:
                # A deletion timestamp or false Ready condition does not mean
                # the process has exited and finished its final flush.
                remaining.append({"name": metadata["name"], "uid": metadata["uid"],
                    "owners": sorted(owners), "deletion_timestamp": metadata.get("deletionTimestamp")})
            elif "Job" in owners:
                completed_jobs.append(metadata["uid"])
        proof["polls"].append({"utc": time.time(), "remaining_writers": remaining,
                               "completed_job_uids": sorted(completed_jobs)})
        expired = bool(remaining) and time.monotonic() >= deadline
        proof["passed"] = not remaining
        if not remaining or expired:
            proof["finished_utc"] = time.time()
        temporary = output / "shutdown.tmp"
        temporary.write_text(json.dumps(proof, indent=2) + "\n")
        temporary.replace(output / "shutdown.json")
        if not remaining:
            return
        if expired:
            raise TimeoutError("Campaign pods still have writers after the shutdown deadline; original state retained")
        time.sleep(1)


def collect_pod_logs(prefix, namespace, pods, output, previous_only=False):
    """Retain log generations separately; unavailable history remains explicit."""
    output.mkdir(parents=True, exist_ok=True)
    manifest_path = output / "pod-log-coverage.json"
    manifest = (json.loads(manifest_path.read_text()) if manifest_path.exists()
                else {"namespace": namespace, "logs": [], "instances": []})
    if manifest["namespace"] != namespace:
        raise RuntimeError("Log evidence belongs to another campaign namespace")
    instances = {(row["pod_uid"], row["container"]): row for row in manifest["instances"]}
    for pod in pods:
        name = pod["metadata"]["name"]
        uid = pod["metadata"]["uid"]
        statuses = {item["name"]: item for item in pod.get("status", {}).get("containerStatuses", [])}
        for container in pod["spec"]["containers"]:
            container_name = container["name"]
            if not all(re.fullmatch(r"[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?", value)
                       for value in (name, uid, container_name)):
                raise RuntimeError("Invalid Kubernetes log identity")
            status = statuses.get(container_name, {})
            restart = status.get("restartCount", 0)
            instances[(uid, container_name)] = {"pod": name, "pod_uid": uid,
                "container": container_name, "latest_restart_index": restart}
            generations = ([(restart, False)] if not previous_only else [])
            if restart:
                generations.append((restart - 1, True))
            for index, previous in generations:
                if previous and any(row["pod_uid"] == uid and row["container"] == container_name
                        and row["restart_index"] == index and row["previous"] and row["exit_code"] == 0
                        for row in manifest["logs"]):
                    continue
                directory = output / "pod-logs" / uid / container_name
                directory.mkdir(parents=True, exist_ok=True)
                path = directory / f"{index}-{'previous' if previous else 'current'}-{time.time_ns()}.log"
                command = prefix + ["logs", name, "-c", container_name, "-n", namespace, "--timestamps"]
                if previous:
                    command.append("--previous")
                with path.open("w") as stream:
                    try:
                        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT,
                                                check=False, timeout=30)
                        code = result.returncode
                    except subprocess.TimeoutExpired:
                        stream.write("\nLog collection exceeded its 30-second deadline.\n")
                        code = 124
                row = {"pod": name, "pod_uid": uid, "container": container_name,
                       "restart_index": index, "previous": previous, "exit_code": code,
                       "container_id": (status.get("lastState", {}).get("terminated", {}).get("containerID")
                                        if previous else status.get("containerID")),
                       "captured_utc": time.time(), "path": str(path.relative_to(output)),
                       "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                manifest["logs"].append(row)
                if not previous:
                    # Keep the existing diagnostic filename as a convenience copy.
                    # The UID/restart-specific original above is never overwritten.
                    shutil.copyfile(path, output / f"{name}-{container_name}.log")
    manifest["instances"] = list(instances.values())
    missing = []
    current_identities = {(pod["metadata"]["uid"], container["name"])
                          for pod in pods for container in pod["spec"]["containers"]}
    for instance in manifest["instances"]:
        observed = {row["restart_index"] for row in manifest["logs"]
                    if row["pod_uid"] == instance["pod_uid"] and row["container"] == instance["container"]
                    and row["exit_code"] == 0 and
                    (row["previous"] or row["restart_index"] == instance["latest_restart_index"])}
        absent = sorted(set(range(instance["latest_restart_index"] + 1)) - observed)
        retired = (instance["pod_uid"], instance["container"]) not in current_identities
        if absent or retired:
            missing.append(instance | {"restart_indices": absent,
                "pod_no_longer_observed": retired})
    manifest.update(missing_instances=missing, complete=not missing, updated_utc=time.time())
    temporary = manifest_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n")
    temporary.replace(manifest_path)
    return manifest


def audit_runtime_logs(output, started_utc, finished_utc):
    """Audit retained runtime instances, including errors during healthy traffic."""
    manifest = json.loads((output / "pod-log-coverage.json").read_text())
    instances = [row for row in manifest["instances"] if row["container"] == "runtime"]
    missing = [row for row in manifest["missing_instances"] if row["container"] == "runtime"]
    canonical = {}
    for row in manifest["logs"]:
        if row["container"] != "runtime" or row["exit_code"] != 0:
            continue
        key = row["pod_uid"], row["restart_index"]
        rank = row["previous"], row["captured_utc"]
        if key not in canonical or rank > (canonical[key]["previous"], canonical[key]["captured_utc"]):
            canonical[key] = row
    observations = []
    for row in canonical.values():
        content = (output / row["path"]).read_bytes()
        text = content.decode(errors="replace")
        errors = []
        for line in text.splitlines():
            timestamp, separator, message = line.partition(" ")
            if not separator:
                continue
            try:
                when = datetime.fromisoformat(timestamp.replace("Z", "+00:00")).timestamp()
            except ValueError:
                continue
            if started_utc <= when <= finished_utc and message.lstrip().startswith(
                    ("fail:", "crit:", "Unhandled exception", "Fatal error")):
                errors.append(line)
        observations.append(row | {"hash_matches_capture": hashlib.sha256(content).hexdigest() == row["sha256"],
            "managed_oom_log_occurrences": text.count("OutOfMemoryException"),
            "runtime_errors_during_workload": errors})
    complete = len(instances) == 3 and not missing and all(row["hash_matches_capture"] for row in observations)
    return {"passed": complete and bool(observations) and not any(
                row["managed_oom_log_occurrences"] or row["runtime_errors_during_workload"] for row in observations),
            "runtime_log_coverage_complete": complete, "missing_instances": missing,
            "workload_window_utc": [started_utc, finished_utc], "instances": observations,
            "note": "One canonical snapshot per pod UID/restart; previous complete logs supersede partial current snapshots. OOM occurrences are not distinct exceptions. Error-level, critical and unhandled errors are counted only within the supplied healthy-workload window; bootstrap errors remain retained in the raw logs."}


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("action", choices=("launch", "wait", "collect", "stop"))
    parser.add_argument("--kubeconfig", type=Path, required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--image")
    parser.add_argument("--controller-image", default="localhost/slimfaas-pr506:candidate-tools")
    parser.add_argument("--suite", choices=("http", "memory", "chaos"), default="chaos")
    parser.add_argument("--probes", choices=("normal", "endurance"), default="endurance")
    parser.add_argument("--scenarios", default="payloads,api,files,wake,retries,chaos,restarts")
    parser.add_argument("--roles", default="follower,leader,quorum")
    parser.add_argument("--durations", default="1,5,10")
    parser.add_argument("--source-commit", default="HEAD")
    parser.add_argument("--variant", default="after")
    parser.add_argument("--ordinal", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=14400)
    options, options.workload_args = parser.parse_known_args()
    if len(options.namespace) > 63 or not re.fullmatch(r"slimfaas-pr506-[a-z0-9-]+", options.namespace):
        parser.error("A dedicated slimfaas-pr506-* namespace of at most 63 characters is required")
    prefix = ["kubectl", "--kubeconfig", str(options.kubeconfig.resolve()), "--context", "kind-slimfaas-pr506"]

    def kubectl(*args, **kwargs):
        return subprocess.run(prefix + list(args), text=True, check=True, **kwargs)

    if options.action == "launch":
        if not options.image:
            parser.error("--image is required to launch")
        options.source_commit = subprocess.check_output(
            ["git", "rev-parse", "--verify", options.source_commit + "^{commit}"], cwd=ROOT, text=True).strip()
        existing = kubectl("get", "namespace", options.namespace, "--ignore-not-found", "-o", "json", capture_output=True)
        if existing.stdout.strip():
            raise RuntimeError("Namespace already exists; retain it and choose a new campaign identity")
        options.output.mkdir(parents=True, exist_ok=False)
        items, config = resources(options)
        (options.output / "resources.json").write_text(json.dumps({"apiVersion": "v1", "kind": "List", "items": items}, indent=2))
        (options.output / "backend.json").write_text(json.dumps(config, indent=2))
        kubectl("apply", "-f", str(options.output / "resources.json"))
        return 0
    owned = json.loads(kubectl("get", "namespace", options.namespace, "-o", "json", capture_output=True).stdout)
    if owned["metadata"].get("labels", {}).get("slimfaas-campaign") != options.namespace:
        raise RuntimeError("Refusing to operate on an unowned namespace")
    if options.action == "wait":
        deadline = time.monotonic() + options.timeout
        while time.monotonic() < deadline:
            job = json.loads(kubectl("get", "job", "campaign-driver", "-n", options.namespace, "-o", "json", capture_output=True).stdout)
            status = job.get("status", {})
            if status.get("succeeded"):
                return 0
            if status.get("failed"):
                return 1
            print(json.dumps({"namespace": options.namespace, "job_status": status, "utc": time.time()}), flush=True)
            time.sleep(30)
        raise TimeoutError("Campaign job exceeded its explicit deadline; original resources are retained")
    if options.action == "stop":
        for kind in ("deployment", "statefulset"):
            kubectl("scale", kind, "--selector", "slimfaas-campaign=" + options.namespace,
                    "--replicas=0", "-n", options.namespace)
        wait_for_campaign_quiescence(kubectl, options.namespace, options.output)
        return 0
    options.output.mkdir(parents=True, exist_ok=True)
    for kind in ("pods", "events", "jobs", "pvc", "statefulsets", "deployments"):
        data = kubectl("get", kind, "-n", options.namespace, "-o", "json", capture_output=True).stdout
        (options.output / f"kubernetes-{kind}.json").write_text(data)
    pods = json.loads((options.output / "kubernetes-pods.json").read_text())["items"]
    collect_pod_logs(prefix, options.namespace, pods, options.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
