#!/usr/bin/env python3
"""Deploy, observe and retain an isolated kind campaign using its explicit kubeconfig."""

import argparse
import json
from pathlib import Path
import re
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
        metadata = {"name": name, "labels": labels | {"app": name}}
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
        result.append(deployment("benchmark-latency", ["/tools/benchmark/SlimFaasBenchmark"],
                                 ["target", "--port", "5000", "--host", "0.0.0.0"], common))
        scale = {"ReplicaMax": 4, "ScrapeIntervalMilliseconds": 1000,
            "Triggers": [{"MetricType": "AverageValue", "MetricName": "benchmark_scale_pressure",
                          "Query": "max_over_time(slimfaas_benchmark_scale_pressure[30s])", "Threshold": 1}],
            "Behavior": {"ScaleUp": {"StabilizationWindowSeconds": 0, "Policies": []},
                         "ScaleDown": {"StabilizationWindowSeconds": 0, "Policies": []}}}
        result.append(deployment("benchmark-scale", ["/tools/benchmark/SlimFaasBenchmark"],
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
            "--runtime", "/app/SlimFaas", "--benchmark", "/tools/benchmark/SlimFaasBenchmark",
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
    if not re.fullmatch(r"slimfaas-pr506-[a-z0-9-]+", options.namespace):
        parser.error("A dedicated slimfaas-pr506-* namespace is required")
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
            kubectl("scale", kind, "--all", "--replicas=0", "-n", options.namespace)
        return 0
    options.output.mkdir(parents=True, exist_ok=True)
    for kind in ("pods", "events", "jobs", "pvc", "statefulsets", "deployments"):
        data = kubectl("get", kind, "-n", options.namespace, "-o", "json", capture_output=True).stdout
        (options.output / f"kubernetes-{kind}.json").write_text(data)
    pods = json.loads((options.output / "kubernetes-pods.json").read_text())["items"]
    for pod in pods:
        for container in pod["spec"]["containers"]:
            path = options.output / f"{pod['metadata']['name']}-{container['name']}.log"
            with path.open("w") as output:
                subprocess.run(prefix + ["logs", pod["metadata"]["name"], "-c", container["name"], "-n", options.namespace],
                               stdout=output, stderr=subprocess.STDOUT, check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
