#!/usr/bin/env python3
"""Run one attributable workload; persistent state and failed evidence are retained."""

import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import importlib.util
import json
import math
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


def driver_hashes(path):
    candidates = {path, *path.parent.glob("*.dll"), *path.parent.glob("*.deps.json"), *path.parent.glob("*.runtimeconfig.json")}
    return {str(candidate): digest(candidate) for candidate in sorted(candidates) if candidate.is_file()}


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
            "restartPolicy": "always", "environment": {"PORT": "31080", "CHAOS_FUNCTION": function}}}}
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
    if options.suite == "features":
        result["functions"]["memory-function-2"] = {"debugUrl": "http://127.0.0.1:31081", "annotations": annotations.copy()}
        result["processes"]["benchmark-target-2"] = {"command": target, "workingDirectory": str(ROOT),
            "restartPolicy": "always", "environment": {"PORT": "31081", "CHAOS_FUNCTION": "memory-function-2"}}
        result["jobs"] = {"chaos-job": {"command": [sys.executable,
            str(ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py"), "--journal-directory", str(options.output / "receipts"), "--job"],
            "workingDirectory": str(ROOT), "annotations": {"SlimFaas/Job": "true", "SlimFaas/DefaultVisibility": "Public",
            "SlimFaas/NumberParallelJob": "16"}, "ttlSecondsAfterFinished": 3600, "backoffLimit": 0}}
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
        next_process_sample = time.monotonic()
        while not stop.is_set():
            started = time.monotonic()
            utc = datetime.now(timezone.utc).isoformat()
            current_phase = phase["value"]
            if (options.output / "phase").exists():
                current_phase = (options.output / "phase").read_text().strip()
            samples = []
            controls = getattr(options, "node_controls", [])
            collect_process = bool(controls) and started >= next_process_sample
            if collect_process:
                next_process_sample = started + 5
            for index, node in enumerate(nodes):
                try:
                    with HTTP.open(node + "/metrics", timeout=2) as response:
                        raw = response.read().decode()
                    values = metric_values(raw)
                    rss = values.get("process_working_set_bytes")
                    rss_kb = int(rss / 1024) if rss is not None and math.isfinite(rss) and rss > 0 else ""
                    memory.write(f"{utc},slimfaas-{index},0,{rss_kb},0,{current_phase}\n")
                    sample = {"node": index, "raw": raw}
                    if collect_process:
                        try:
                            with HTTP.open(controls[index] + "/stats", timeout=2) as response:
                                sample["process_statistics"] = json.load(response)
                        except OSError as error:
                            sample["process_statistics_error"] = str(error)
                    samples.append(sample)
                except OSError as error:
                    # Keep failed scrapes in the CSV, including an entirely
                    # missing round, so the comparer cannot silently omit it.
                    memory.write(f"{utc},slimfaas-{index},0,,0,{current_phase}\n")
                    samples.append({"node": index, "error": str(error)})
            output.write(json.dumps({"utc": utc, "phase": current_phase, "nodes": samples,
                                     "disk_free": shutil.disk_usage(options.output).free}) + "\n")
            output.flush()
            memory.flush()
            stop.wait(max(0, 1 - (time.monotonic() - started)))


def verify_receipts(document_path, directory, timeout=60, *, allow_duplicates=False):
    document = json.loads(document_path.read_text())
    report = document["report"]
    expected = {f"{report['KeyPrefix']}-async-{row['Worker']}-{row['Sequence']}" for row in document["samples"]
                if row["operation"] == "Async" and row["Success"]}
    body = bytes(ord("a") + index % 23 for index in range(report["PayloadBytes"]))
    checksum = hashlib.sha256(body).hexdigest()
    deadline = time.monotonic() + timeout
    while True:
        observations = {}
        failed_attempts = {}
        for path in directory.glob("*.jsonl"):
            for line in path.read_bytes().splitlines(keepends=True):
                if not line.endswith(b"\n"):
                    continue
                row = json.loads(line)
                if row.get("id") not in expected or row.get("event") != "completed":
                    continue
                if row["bytes"] != len(body) or row["sha256"] != checksum:
                    raise AssertionError(f"Async receipt corruption: {row}")
                if not 200 <= row["status"] < 300:
                    failed_attempts[row["id"]] = 1 + failed_attempts.get(row["id"], 0)
                    continue
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
              "failed_http_attempts": failed_attempts, "payload_sha256": checksum,
              "passed": not missing and (allow_duplicates or not duplicates)}
    if allow_duplicates:
        result["duplicate_policy"] = "Count separately during controlled faults; compare attempts/executions before acceptance"
    document_path.with_suffix(".receipts.json").write_text(json.dumps(result, indent=2))
    if not result["passed"]:
        raise AssertionError(f"MemoryLab receipts failed: {len(missing)} missing, {len(duplicates)} duplicate IDs")


DATA_SCENARIOS = ("slimdata-set", "get", "set", "delete", "hashset", "hashget", "hashdelete",
                  "increment", "decrement", "files-upload", "files-download", "files", "files-unique")


def capacity_guard(output):
    if shutil.disk_usage(output).free < 20 * 1024**3:
        raise RuntimeError("Free disk below 20 GiB; preserve this campaign and resume in a fresh attempt after reclaiming inactive caches")


def invoke_case(command, destination, statuses, warmup=None):
    capacity_guard(destination.parent)
    destination.mkdir()
    (destination / "command.json").write_text(json.dumps(command, indent=2))
    if warmup is not None:
        (destination / "warmup").mkdir()
        with (destination / "warmup" / "workload.log").open("w") as log:
            warmup_code = subprocess.call(warmup, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
        if warmup_code:
            statuses.append({"suite": destination.name + "-warmup", "exit_code": warmup_code})
            (destination / "status.json").write_text(json.dumps({"passed": False, "phase": "warmup", "exit_code": warmup_code}))
            return warmup_code
    with (destination / "workload.log").open("w") as log:
        code = subprocess.call(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
    result = {"suite": destination.name, "exit_code": code}
    statuses.append(result)
    (destination / "status.json").write_text(json.dumps({"passed": code == 0, **result}))
    return code


def run_data_suite(options, nodes, statuses):
    for scenario in DATA_SCENARIOS:
        sizes = options.payload_bytes.split(",") if scenario.startswith("files") else ["4096"]
        for size in sizes:
            for concurrency in options.concurrency.split(","):
                for repetition in range(1, options.repetitions + 1):
                    case = options.output / f"{scenario}-b{size}-c{concurrency}-r{repetition}"
                    command = executable(options.memorylab) + ["load", "--scenario", scenario,
                        "--node-urls", ",".join(nodes), "--duration", str(options.memory_duration),
                        "--concurrency", concurrency, "--payload-bytes", size, "--file-bytes", size, "--repetition", str(repetition),
                        "--key-prefix", case.name + "-" + options.output.name, "--validate", "true", "--cleanup-owned-files", "true",
                        "--json", str(case / "operations.json"), "--csv", str(case / "operations.csv"),
                        "--phase-file", str(options.output / "phase")]
                    if options.data_journal:
                        command += ["--journal-directory", str(case / "requests")]
                    warmup = command.copy()
                    for option, value in (("--duration", str(options.memory_warmup)), ("--key-prefix", case.name + "-warmup"),
                        ("--json", str(case / "warmup/warmup.json")), ("--csv", str(case / "warmup/warmup.csv")),
                        ("--journal-directory", str(case / "warmup/requests"))):
                        if option in warmup:
                            warmup[warmup.index(option) + 1] = value
                    invoke_case(command, case, statuses, warmup=warmup if options.memory_warmup > 0 else None)


def run_feature_suite(options, nodes, config, receipts, statuses):
    def cases(scenario, functions, language="http"):
        sizes = options.payload_bytes.split(",") if scenario.startswith("ws-") else ["4096"]
        for size in sizes:
            for concurrency in options.concurrency.split(","):
                for repetition in range(1, options.repetitions + 1):
                    case = options.output / f"{scenario}-{language}-f{len(functions)}-b{size}-c{concurrency}-r{repetition}"
                    command = executable(options.benchmark) + ["features", "--scenario", scenario, "--label", f"{scenario}/{language}/fanout-{len(functions)}",
                        "--node-urls", ",".join(nodes), "--functions", ",".join(functions), "--receipts", str(receipts),
                        "--messages", "200", "--concurrency", concurrency, "--payload-bytes", size, "--event", f"benchmark-event-{language}",
                        "--repetition", str(repetition), "--drain-timeout", str(options.feature_drain_timeout), "--output", str(case)]
                    invoke_case(command, case, statuses)
    cases("streams", ["memory-function"])
    cases("streams", ["memory-function", "memory-function-2"])
    cases("jobs", ["memory-function"])
    for language in ("dotnet", "python"):
        receivers = []
        logs = []
        names = [f"benchmark-{language}-{index}" for index in range(2)]
        try:
            for index, name in enumerate(names):
                url = config["nodes"][index]["websocket"] if config else nodes[index].replace("http:", "ws:") + "/ws"
                command = executable(options.benchmark) + ["sdk-target"] if language == "dotnet" else [
                    sys.executable, str(ROOT / ".bin/benchmark-sdk-target.py")]
                command += ["--url", url, "--function", name, "--receipts", str(receipts), "--event", f"benchmark-event-{language}"]
                log = (options.output / f"{name}.log").open("w")
                logs.append(log)
                env = os.environ | {"PYTHONPATH": str(ROOT / "client/python/slimfaas-client/src")}
                receivers.append(subprocess.Popen(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True))
            def registered():
                if any(process.poll() is not None for process in receivers):
                    raise RuntimeError("Official SDK receiver exited before registration")
                for node in nodes:
                    with HTTP.open(node + "/status-functions", timeout=5) as response:
                        found = {row["Name"] for row in json.load(response) if row.get("NumberReady", 0) > 0}
                    if not set(names).issubset(found):
                        return False
                return True
            LOCAL.wait_until("official SDK receivers registered on every node", registered, 120)
            cases("ws-sync", names[:1], language)
            cases("ws-async", names[:1], language)
            cases("ws-events", names, language)
        finally:
            for process in receivers:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=10)
            for log in logs:
                log.close()


def prepare_fresh_wal(state_root, chunk_bytes):
    if chunk_bytes not in (4096, 8192, 16384, 32768, 65536):
        raise ValueError("Unsupported benchmark WAL geometry")
    paths = [state_root / "data" / f"slimfaas-{index}" / "wal" for index in range(3)]
    if any(path.exists() for path in paths):
        raise ValueError("WAL tuning requires fresh volumes; never change an existing WAL marker")
    for path in paths:
        path.mkdir(parents=True, exist_ok=False)
        with (path / "slimdata-chunk-size").open("x") as marker:
            marker.write(str(chunk_bytes))


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
    metadata = {"schema_version": 2, "source_commit": config.get("source_commit") if config else getattr(options, "source_commit", "unknown"), "started_utc": time.time(), "arguments": {k: str(v) if isinstance(v, Path) else v for k, v in vars(options).items()},
                "runtime_sha256": digest(options.runtime) if config is None else None,
                "runtime_files": runtime_hashes(options.runtime) if config is None else None,
                "controller_runtime_sha256": digest(options.runtime) if config else None,
                "driver_sha256": digest(options.benchmark), "driver_files": driver_hashes(options.benchmark),
                "memorylab_files": driver_hashes(options.memorylab),
                "sdk_fixture_sha256": digest(ROOT / ".bin/benchmark-sdk-target.py"),
                "receipt_fixture_sha256": digest(ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py"),
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
            ensure_ports_free([31020, 31021, 31022, 31023, 31080, 31081, 3162, 3163, 3164, *range(32000, 32016)])
            if getattr(options, "wal_chunk_bytes", None) is not None:
                prepare_fresh_wal(options.output / "state", options.wal_chunk_bytes)
            manifest = options.output / "manifest.yaml"
            manifest.write_text(LOCAL.manifest_yaml(local_manifest(options)))
            env = os.environ | {"OpenTelemetry__Enable": "false"}
            if getattr(options, "async_offload_threshold", None) is not None:
                env["SlimFaas__AsyncBodyOffloadThresholdBytes"] = str(options.async_offload_threshold)
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
            if options.suite == "features":
                for node in nodes:
                    with HTTP.open(node + "/status-functions", timeout=2) as response:
                        ready_names = {row["Name"] for row in json.load(response) if row.get("NumberReady", 0) > 0}
                    if not {"memory-function", "memory-function-2"}.issubset(ready_names):
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
            if options.http_journal:
                command += ["--journal-directory", str(options.output / "requests")]
            with (options.output / "workload.log").open("w") as output:
                code = subprocess.call(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
            statuses.append({"suite": "http", "exit_code": code})
        elif options.suite == "data":
            run_data_suite(options, nodes, statuses)
        elif options.suite == "features":
            run_feature_suite(options, nodes, config, receipts, statuses)
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
                    "--journal-directory", str(destination.parent / (label + "-requests")),
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
    parser.add_argument("--suite", choices=("http", "memory", "data", "features"), required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--benchmark", type=Path, required=True)
    parser.add_argument("--memorylab", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--backend-config", type=Path)
    parser.add_argument("--source-commit", default="unknown")
    parser.add_argument("--variant", default="after")
    parser.add_argument("--ordinal", type=int, default=1)
    parser.add_argument("--wal-chunk-bytes", type=int, choices=(4096, 8192, 16384, 32768, 65536))
    parser.add_argument("--async-offload-threshold", type=int, choices=(131072, 262144, 524288, 1048576))
    parser.add_argument("--data-journal", action=argparse.BooleanOptionalAction, default=True,
                        help="Record isolated data transport exchanges in addition to operation samples")
    parser.add_argument("--feature-drain-timeout", type=int, default=300)
    parser.add_argument("--http-journal", action=argparse.BooleanOptionalAction, default=True,
                        help="Record HTTP transport exchanges in addition to compressed latency samples")
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
