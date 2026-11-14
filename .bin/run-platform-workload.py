#!/usr/bin/env python3
"""Run one attributable workload; persistent state and failed evidence are retained."""

import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
from urllib.request import build_opener, ProxyHandler


ROOT = Path(__file__).resolve().parents[1]
HTTP = build_opener(ProxyHandler({}))
spec = importlib.util.spec_from_file_location("local_campaign", ROOT / ".bin/test-slimdata-local-chaos.py")
LOCAL = importlib.util.module_from_spec(spec)
spec.loader.exec_module(LOCAL)


def executable(path):
    return ["dotnet", str(path)] if path.suffix == ".dll" else [str(path)]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def runtime_hashes(path):
    candidates = [path, path.with_suffix(".dll"), path.parent / "SlimData.dll",
                  path.parent / "DotNext.Net.Cluster.dll"]
    return {str(candidate): digest(candidate) for candidate in candidates if candidate.is_file()}


def local_manifest(options):
    driver = executable(options.benchmark)
    annotations = {"SlimFaas/Function": "true", "SlimFaas/NumberParallelRequest": "256",
                   "SlimFaas/NumberParallelRequestPerPod": "256", "SlimFaas/ReplicasAtStart": "1",
                   "SlimFaas/ReplicasMin": "1"}
    function = "benchmark-latency" if options.suite == "http" else "memory-function"
    if options.suite == "memory":
        annotations.update({"SlimFaas/NumberParallelRequest": "64", "SlimFaas/NumberParallelRequestPerPod": "64"})
    target = driver + ["target", "--port", "31080"] if options.suite == "http" else [
        sys.executable, str(ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py"),
        "--journal-directory", str(options.output / "receipts")]
    result = {"schemaVersion": 1, "name": "platform-" + options.output.name,
        "cluster": {"nodes": 3, "entrypointPort": 31020, "nodeHttpPortBase": 31021,
                    "raftPortBase": 3162, "nodeLogLevel": "Warning"},
        "processPorts": {"from": 32000, "to": 32015},
        "state": {"mode": "persistent", "directory": str(options.output / "state")},
        "functions": {function: {"debugUrl": "http://127.0.0.1:31080", "annotations": annotations}},
        "processes": {"benchmark-target": {"command": target, "workingDirectory": str(ROOT),
            "restartPolicy": "always", "environment": {"PORT": "31080"}}}}
    if options.suite == "http":
        scale = {"ReplicaMax": 4, "ScrapeIntervalMilliseconds": 1000,
            "Triggers": [{"MetricType": "AverageValue", "MetricName": "benchmark_scale_pressure",
                          "Query": "max_over_time(slimfaas_benchmark_scale_pressure[30s])", "Threshold": 1}],
            "Behavior": {"ScaleUp": {"StabilizationWindowSeconds": 0, "Policies": []},
                         "ScaleDown": {"StabilizationWindowSeconds": 0, "Policies": []}}}
        result["functions"]["benchmark-scale"] = {"command": driver + ["target"], "workingDirectory": str(ROOT),
            "annotations": {"SlimFaas/Function": "true", "SlimFaas/TimeoutSecondBeforeSetReplicasMin": "5",
                "SlimFaas/NumberParallelRequest": "4", "SlimFaas/NumberParallelRequestPerPod": "1",
                "SlimFaas/ReplicasAtStart": "1", "SlimFaas/ReplicasMin": "0",
                "prometheus.io/path": "/metrics", "prometheus.io/port": "{port}", "prometheus.io/scrape": "true",
                "SlimFaas/Scale": json.dumps(scale)},
            "health": {"path": "/health", "periodSeconds": 1, "timeoutSeconds": 2, "startupTimeoutSeconds": 120}}
    return result


def metric_values(raw):
    result = {}
    for line in raw.splitlines():
        if line and not line.startswith("#"):
            fields = line.rsplit(" ", 1)
            if len(fields) == 2:
                try:
                    result[fields[0]] = float(fields[1])
                except ValueError:
                    pass
    return result


def snapshot(options, nodes, config, phase):
    for index, node in enumerate(nodes):
        with HTTP.open(node + "/metrics", timeout=5) as response:
            (options.output / f"metrics-{phase}-slimfaas-{index}.prom").write_bytes(response.read())
        root = Path(config["node_state_roots"][index]) if config else options.output / "state/data" / f"slimfaas-{index}"
        entries = [f"{path.stat().st_size} {path.relative_to(root)}" for path in (root / "wal").rglob("*") if path.is_file()]
        (options.output / f"wal-files-{phase}-slimfaas-{index}.txt").write_text("\n".join(entries))
        snapshots = [{"name": str(path.relative_to(root)), "bytes": path.stat().st_size}
                     for path in (root / "db").rglob("*") if path.is_file()]
        (options.output / f"snapshots-{phase}-slimfaas-{index}.json").write_text(json.dumps(snapshots, indent=2))


def ensure_ports_free(ports):
    for port in ports:
        with socket.socket() as probe:
            probe.settimeout(.2)
            if probe.connect_ex(("127.0.0.1", port)) == 0:
                raise RuntimeError(f"Port {port} already has a listener; refusing to attach to another workload")


def monitor(options, nodes, stop, phase):
    with gzip.open(options.output / "metrics.jsonl.gz", "wt") as output, (options.output / "memory.csv").open("w") as memory:
        memory.write("timestamp,node,pid,rss_kb,vsz_kb,phase\n")
        while not stop.is_set():
            started = time.monotonic()
            utc = datetime.now(timezone.utc).isoformat()
            current_phase = phase["value"]
            if (options.output / "phase").exists():
                current_phase = (options.output / "phase").read_text().strip()
            samples = []
            for index, node in enumerate(nodes):
                try:
                    with HTTP.open(node + "/metrics", timeout=2) as response:
                        raw = response.read().decode()
                    values = metric_values(raw)
                    rss = values.get("slimdata_process_working_set_bytes")
                    if rss is not None:
                        memory.write(f"{utc},slimfaas-{index},0,{int(rss / 1024)},0,{current_phase}\n")
                    samples.append({"node": index, "raw": raw})
                except OSError as error:
                    samples.append({"node": index, "error": str(error)})
            output.write(json.dumps({"utc": utc, "phase": current_phase, "nodes": samples,
                                     "disk_free": shutil.disk_usage(options.output).free}) + "\n")
            output.flush()
            memory.flush()
            stop.wait(max(0, 1 - (time.monotonic() - started)))


def verify_receipts(document_path, directory, timeout=60):
    document = json.loads(document_path.read_text())
    report = document["report"]
    expected = {f"{report['KeyPrefix']}-async-{row['Worker']}-{row['Sequence']}" for row in document["samples"]
                if row["operation"] == "Async" and row["Success"]}
    body = bytes(ord("a") + index % 23 for index in range(report["PayloadBytes"]))
    checksum = hashlib.sha256(body).hexdigest()
    deadline = time.monotonic() + timeout
    while True:
        observations = {}
        for path in directory.glob("*.jsonl"):
            for line in path.read_bytes().splitlines(keepends=True):
                if not line.endswith(b"\n"):
                    continue
                row = json.loads(line)
                if row.get("id") not in expected or row.get("event") != "completed":
                    continue
                if row["bytes"] != len(body) or row["sha256"] != checksum or row["status"] >= 300:
                    raise AssertionError(f"Async receipt corruption: {row}")
                observations[row["id"]] = 1 + observations.get(row["id"], 0)
        if not expected - observations.keys() or time.monotonic() >= deadline:
            break
        # Queue diagnostics are sampled, so a zero gauge can precede the last
        # real HTTP delivery. Wait only for read-only independent receipts;
        # never retry a mutation or synthesize an observation.
        time.sleep(min(.1, max(0, deadline - time.monotonic())))
    missing = sorted(expected - observations.keys())
    duplicates = {key: count for key, count in observations.items() if count > 1}
    result = {"accepted": len(expected), "observed": len(observations), "missing": missing, "duplicates": duplicates,
              "payload_sha256": checksum, "passed": not missing and not duplicates}
    document_path.with_suffix(".receipts.json").write_text(json.dumps(result, indent=2))
    if not result["passed"]:
        raise AssertionError(f"MemoryLab receipts failed: {len(missing)} missing, {len(duplicates)} duplicate IDs")


def run(options):
    options.output.mkdir(parents=True, exist_ok=False)
    if shutil.disk_usage(options.output).free < 20 * 1024**3:
        raise RuntimeError("Less than 20 GiB free; retain evidence and reclaim verified inactive caches before continuing")
    runtime = executable(options.runtime)
    config = json.loads(options.backend_config.read_text()) if options.backend_config else None
    nodes = [node["http"] for node in config["nodes"]] if config else [f"http://127.0.0.1:{31021+i}" for i in range(3)]
    entry = config["entry"] if config else "http://127.0.0.1:31020"
    direct = config["direct"] if config else "http://127.0.0.1:31080"
    receipts = Path(config["receipts_root"]) if config else options.output / "receipts"
    metadata = {"started_utc": time.time(), "arguments": {k: str(v) if isinstance(v, Path) else v for k, v in vars(options).items()},
                "runtime_sha256": digest(options.runtime) if config is None else None,
                "runtime_files": runtime_hashes(options.runtime) if config is None else None,
                "controller_runtime_sha256": digest(options.runtime) if config else None,
                "driver_sha256": digest(options.benchmark),
                "workload_sha256": digest(Path(__file__)),
                "local_harness_sha256": digest(Path(LOCAL.__file__)),
                "memorylab_sha256": digest(options.memorylab), "backend": config or "Local", "pid_source": "Prometheus (PID not exported)"}
    (options.output / "parameters.json").write_text(json.dumps(metadata, indent=2))
    process = None
    log = None
    stop = threading.Event()
    phase = {"value": "validation"}
    sampler = None
    statuses = []
    passed = False
    error = None
    try:
        if config is None:
            ensure_ports_free([31020, 31021, 31022, 31023, 31080, 3162, 3163, 3164, *range(32000, 32016)])
            manifest = options.output / "manifest.yaml"
            manifest.write_text(LOCAL.manifest_yaml(local_manifest(options)))
            env = os.environ | {"OpenTelemetry__Enable": "false"}
            if options.suite == "memory":
                env.update({"SlimData__SnapshotIntervalEntries": "50", "SlimData__SnapshotIntervalBytes": "8388608"})
            log = (options.output / "supervisor.log").open("w")
            process = subprocess.Popen(runtime + ["local", "up", "-f", str(manifest)], cwd=ROOT,
                                       env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        def ready():
            if process and process.poll() is not None:
                raise RuntimeError("Owned Local supervisor exited before readiness")
            for url in [entry + "/ready", direct + "/health"] + [node + "/ready" for node in nodes]:
                with HTTP.open(url, timeout=2) as response:
                    if response.status != 200:
                        return False
            return True
        LOCAL.wait_until("workload cluster and direct target ready", ready, 180)
        if config:
            identities = []
            for node in config["nodes"]:
                with HTTP.open(node["control"] + "/status", timeout=5) as response:
                    identities.append(json.load(response))
            (options.output / "runtime-identities.json").write_text(json.dumps(identities, indent=2))
        sampler = threading.Thread(target=monitor, args=(options, nodes, stop, phase))
        sampler.start()
        if options.suite == "http":
            command = executable(options.benchmark) + ["run", "--profile", "async-queue", "--slimfaas-url", entry,
                "--direct-url", direct, "--node-urls", ",".join(nodes), "--duration", str(options.duration),
                "--warmup", str(options.warmup), "--repetitions", str(options.repetitions),
                "--payload-bytes", options.payload_bytes, "--concurrency", options.concurrency,
                "--async-drain-timeout", "180", "--scale-messages", "200", "--scale-concurrency", "32",
                "--scale-timeout", "120", "--output", str(options.output)]
            command += ["--samples-format", "csv.gz"]
            with (options.output / "workload.log").open("w") as output:
                code = subprocess.call(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
            statuses.append({"suite": "http", "exit_code": code})
        else:
            (options.output / "manifest.txt").write_text(
                f"run_label={options.variant}-r{options.ordinal}\nduration_seconds={options.memory_duration}\n")
            for label, seconds in (("warmup", options.memory_warmup), ("load", options.memory_duration)):
                phase["value"] = label
                prefix = options.output.name + "-" + label
                destination = options.output / "warmup" / "warmup.json" if label == "warmup" else options.output / "operations.json"
                destination.parent.mkdir(exist_ok=True)
                if label == "load":
                    snapshot(options, nodes, config, "before")
                command = executable(options.memorylab) + ["load", "--scenario", options.scenario,
                    "--node-urls", ",".join(nodes), "--duration", str(seconds), "--concurrency", str(options.memory_concurrency),
                    "--key-prefix", prefix, "--validate", "true", "--json", str(destination),
                    "--csv", str(destination.with_suffix(".csv")),
                    "--phase-file", str(destination.parent / "phase")]
                with (options.output / f"{label}.log").open("w") as output:
                    code = subprocess.call(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
                statuses.append({"suite": label, "exit_code": code})
                if destination.exists():
                    verify_receipts(destination, receipts)
                if code:
                    raise RuntimeError(f"MemoryLab {label} returned {code}")
            snapshot(options, nodes, config, "after")
        passed = all(row["exit_code"] == 0 for row in statuses)
    except BaseException as failure:
        error = repr(failure)
        raise
    finally:
        stop.set()
        if sampler:
            sampler.join(timeout=10)
        if process and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=60)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
                passed = False
                error = "Owned supervisor did not stop gracefully"
        if log:
            log.close()
        (options.output / "status.json").write_text(json.dumps({"passed": passed, "error": error,
            "statuses": statuses, "finished_utc": time.time()}, indent=2))
    return 0 if passed else 1


def parse_arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=("http", "memory"), required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--benchmark", type=Path, required=True)
    parser.add_argument("--memorylab", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--backend-config", type=Path)
    parser.add_argument("--variant", default="after")
    parser.add_argument("--ordinal", type=int, default=1)
    parser.add_argument("--duration", type=int, default=10)
    parser.add_argument("--warmup", type=int, default=2)
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--payload-bytes", default="64,4096,65536,262144,1048576,2000000,2097152,4194304")
    parser.add_argument("--concurrency", default="1,4,16")
    parser.add_argument("--scenario", choices=("slimdata-set", "slimdata-mixed"), default="slimdata-set")
    parser.add_argument("--memory-concurrency", type=int, default=12)
    parser.add_argument("--memory-duration", type=int, default=120)
    parser.add_argument("--memory-warmup", type=int, default=30)
    options = parser.parse_args()
    for name in ("runtime", "benchmark", "memorylab", "output", "backend_config"):
        value = getattr(options, name)
        if value is not None:
            setattr(options, name, value.resolve())
    return options


if __name__ == "__main__":
    raise SystemExit(run(parse_arguments()))
