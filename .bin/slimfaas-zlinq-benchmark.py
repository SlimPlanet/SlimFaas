#!/usr/bin/env python3
"""Compare four prebuilt runtimes through identical three-node local workloads."""

import argparse
import concurrent.futures
import gzip
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import shutil
import signal
import socket
import statistics
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid


REPO = Path(__file__).resolve().parent.parent
NODES = [f"http://127.0.0.1:{port}" for port in range(31021, 31024)]
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def get(url):
    with HTTP.open(url, timeout=5) as response:
        return response.read().decode()


def resources():
    def read_node(url):
        metrics = {}
        for line in get(url + "/metrics").splitlines():
            if line.startswith(("process_cpu_seconds_total ", "process_working_set_bytes ")):
                name, value = line.split()
                metrics[name] = float(value)
                if not math.isfinite(metrics[name]) or metrics[name] < 0:
                    raise ValueError(f"Invalid resource metric: {name}={value}")
        return metrics["process_cpu_seconds_total"], metrics["process_working_set_bytes"]

    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
        nodes = list(executor.map(read_node, NODES))
    return {"cpu_seconds": sum(row[0] for row in nodes), "rss_bytes": sum(row[1] for row in nodes),
            "host_load_average": os.getloadavg()}


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def run(command, log, **kwargs):
    print("RUN", " ".join(map(str, command)), flush=True)
    with log.open("w") as output:
        subprocess.run(list(map(str, command)), cwd=REPO, stdout=output,
                       stderr=subprocess.STDOUT, check=True, **kwargs)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def data_case(args, directory, scenario, warmup=False):
    directory.mkdir()
    phase = directory / "phase"
    phase.write_text("load")
    command = ["dotnet", REPO / "tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll",
               "load", "--scenario", scenario, "--function", "benchmark-latency",
               "--first-port", "31021", "--nodes", "3", "--concurrency", str(args.data_concurrency),
               "--duration", str(args.warmup if warmup else args.data_duration),
               "--key-prefix", directory.name, "--payload-bytes", "4096", "--file-bytes", str(args.file_bytes),
               "--json", directory / "load.json", "--phase-file", phase,
               "--validate", "true"]
    before = resources()
    samples = [dict(elapsed=0, **before)]
    errors = []
    finished = threading.Event()
    started = time.monotonic()

    def sample():
        try:
            while not finished.wait(0.5):
                if phase.read_text().strip() != "load":
                    break
                samples.append(dict(elapsed=time.monotonic() - started, **resources()))
        except Exception as error:
            errors.append(str(error))

    sampler = threading.Thread(target=sample)
    sampler.start()
    try:
        run(command, directory / "load.log")
    finally:
        finished.set()
        sampler.join()
        save(directory / "resources.json", {"samples": samples, "errors": errors})
    if errors:
        raise RuntimeError(f"Resource sampling failed: {errors}")
    # The driver snapshots all nodes at the load boundary, before state validation.
    cpu_end, rss_end = 0, 0
    for node in range(3):
        lines = (directory / f"metrics-load-end-slimfaas-{node}.prom").read_text().splitlines()
        metrics = dict(line.split() for line in lines if line.startswith(
            ("process_cpu_seconds_total ", "process_working_set_bytes ")))
        cpu_end += float(metrics["process_cpu_seconds_total"])
        rss_end += float(metrics["process_working_set_bytes"])
    report = json.loads((directory / "load.json").read_text())
    completed = report["report"]["Completed"]
    if cpu_end < before["cpu_seconds"]:
        raise ValueError("Runtime CPU counter decreased during data load")
    save(directory / "summary.json", {
        "scenario": scenario, "warmup": warmup, "load": report["report"],
        "payload_bytes": args.file_bytes if scenario == "files" else 4096,
        "cpu_seconds": cpu_end - before["cpu_seconds"],
        "cpu_ms_per_operation": (cpu_end - before["cpu_seconds"]) * 1000 / completed if completed else None,
        "peak_rss_bytes": max([rss_end] + [item["rss_bytes"] for item in samples]),
    })


def stream_case(args, directory):
    """Bounded durable fan-out burst, with target IDs and committed ACK validation."""
    directory.mkdir()
    run_id = "stream-" + uuid.uuid4().hex
    stream = "benchmark-stream"
    base = NODES[0]
    body = bytes(4096)

    def post(url, payload=b"", headers=None):
        request = urllib.request.Request(url, data=payload, headers=headers or {}, method="POST")
        with HTTP.open(request, timeout=60) as response:
            return response.status, response.read()

    post(base + f"/publish-stream/{stream}/echo", body)
    post(base + f"/data/streams/{stream}/functions/benchmark-latency")
    before = resources()
    samples = [before]
    errors = []
    finished = threading.Event()

    def sample():
        try:
            while not finished.wait(0.5):
                samples.append(resources())
        except Exception as error:
            errors.append(str(error))

    def publish(index):
        ticks = time.time_ns() // 100 + 621355968000000000
        started = time.monotonic()
        status, receipt = post(NODES[index % 3] + f"/publish-stream/{stream}/echo", body, {
            "Content-Type": "application/octet-stream",
            "X-SlimFaas-Benchmark-Run-Id": run_id,
            "X-SlimFaas-Benchmark-Request-Id": f"{run_id}-{index}",
            "X-SlimFaas-Benchmark-Sent-Utc-Ticks": str(ticks),
        })
        if status != 202:
            raise RuntimeError(f"Unexpected append response: {status}")
        return json.loads(receipt)["MessageId"], (time.monotonic() - started) * 1000

    sampler = threading.Thread(target=sample)
    sampler.start()
    started = time.monotonic()
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.data_concurrency) as executor:
            receipts = list(executor.map(publish, range(args.stream_messages)))
        deadline = time.monotonic() + 300
        while True:
            delivery = json.loads(get(f"http://127.0.0.1:31080/benchmark/observations/{run_id}"))
            progress = json.loads(get(base + f"/data/streams/{stream}/functions/benchmark-latency"))
            if delivery["uniqueCount"] == args.stream_messages and progress["Lag"] == 0 and progress["PendingCount"] == 0:
                break
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Stream did not drain: {delivery}, {progress}")
            time.sleep(0.25)
        elapsed = time.monotonic() - started
        after = resources()
    finally:
        finished.set()
        sampler.join()
        save(directory / "resources.json", {"samples": samples, "errors": errors})
    if errors or delivery["duplicateCount"] or len({item[0] for item in receipts}) != args.stream_messages:
        raise RuntimeError("Stream correctness or resource sampling failed")
    if after["cpu_seconds"] < before["cpu_seconds"]:
        raise ValueError("Runtime CPU counter decreased during Stream burst")
    latencies = sorted(item[1] for item in receipts)
    save(directory / "summary.json", {
        "scenario": "streams", "completed": len(receipts), "elapsed_seconds": elapsed,
        "rate": len(receipts) / elapsed, "p50_ms": statistics.median(latencies),
        "p95_ms": latencies[int(0.95 * (len(latencies) - 1))],
        "p99_ms": latencies[int(0.99 * (len(latencies) - 1))],
        "cpu_seconds": after["cpu_seconds"] - before["cpu_seconds"],
        "cpu_ms_per_operation": (after["cpu_seconds"] - before["cpu_seconds"]) * 1000 / len(receipts),
        "peak_rss_bytes": max(item["rss_bytes"] for item in samples + [after]),
        "delivery": delivery, "progress": progress,
    })


def benchmark(args, variant, round_number):
    binary = args.publish_root / variant / "SlimFaas"
    directory = args.output / f"round-{round_number}-{variant}"
    directory.mkdir()
    manifest_args = ["-f", REPO / "benchmarks/slimfaas.local.benchmark.yaml"]
    if args.dispatch_parallelism != 256:
        manifest_args += ["-f", args.output / "dispatch-overlay.yaml"]
    for port in [31020, 31021, 31022, 31023, 31080, 3162, 3163, 3164]:
        deadline = time.monotonic() + 20
        while True:
            try:
                with socket.socket() as probe:
                    # Match Kestrel's restart behavior; TIME_WAIT is not a listener.
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    probe.bind(("127.0.0.1", port))
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise RuntimeError(f"Benchmark port {port} remains occupied") from None
                time.sleep(0.1)
    run([binary, "local", "validate", *manifest_args],
        directory / "validate.log")
    local_log = (directory / "local.log").open("w")
    started = time.monotonic()
    process = subprocess.Popen([str(binary), "local", "up", *map(str, manifest_args), "--clean"],
                               cwd=REPO, stdout=local_log, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        deadline = time.monotonic() + 180
        while True:
            if process.poll() is not None:
                raise RuntimeError(f"Local cluster exited: {directory / 'local.log'}")
            try:
                get("http://127.0.0.1:31020/ready")
                get("http://127.0.0.1:31080/health")
                break
            except (OSError, urllib.error.URLError):
                if time.monotonic() >= deadline:
                    raise TimeoutError("Local cluster readiness exceeded 180 seconds") from None
                time.sleep(0.25)
        save(directory / "startup.json", {"ready_seconds": time.monotonic() - started})
        time.sleep(5)
        save(directory / "idle.json", resources())
        processes = subprocess.check_output(["ps", "-axo", "pid,ppid,command"], text=True)
        (directory / "processes.txt").write_text("\n".join(
            line for line in processes.splitlines() if str(args.publish_root) in line) + "\n")
        run(["dotnet", REPO / "benchmarks/SlimFaasBenchmark/bin/Release/net10.0/SlimFaasBenchmark.dll",
             "run", "--profile", "async-queue", "--duration", args.duration, "--warmup", args.warmup,
             "--repetitions", "1", "--payload-bytes", args.payloads, "--concurrency", args.concurrency,
             "--async-drain-timeout", "300", "--scale-timeout", "180", "--output", directory],
            directory / "http.log")
        samples = directory / "latency-samples.csv"
        if samples.exists():
            with samples.open("rb") as source, gzip.open(str(samples) + ".gz", "wb", compresslevel=1) as target:
                shutil.copyfileobj(source, target)
            samples.unlink()
        for scenario in args.data_scenarios.split(",") if args.data_scenarios else []:
            data_case(args, directory / (scenario + "-warmup"), scenario, warmup=True)
            data_case(args, directory / scenario, scenario)
        if args.stream_messages:
            stream_case(args, directory / "streams")
        save(directory / "complete.json", {"variant": variant, "round": round_number})
        print(f"COMPLETE round={round_number} variant={variant}", flush=True)
    finally:
        stop(process)
        local_log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--duration", type=int, default=30)
    parser.add_argument("--warmup", type=int, default=5)
    parser.add_argument("--payloads", default="64,4096,2097152")
    parser.add_argument("--concurrency", default="1,16")
    parser.add_argument("--data-duration", type=int, default=90)
    parser.add_argument("--data-concurrency", type=int, default=16)
    parser.add_argument("--file-bytes", type=int, default=262144)
    parser.add_argument("--data-scenarios", default="slimdata-set,slimdata-mixed,files")
    parser.add_argument("--stream-messages", type=int, default=200)
    parser.add_argument("--dispatch-parallelism", type=int, default=16)
    parser.add_argument("--variants", default="baseline-jit,candidate-jit,candidate-aot,baseline-aot")
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if min(args.rounds, args.duration, args.warmup, args.data_duration, args.data_concurrency) < 1:
        parser.error("rounds, duration, warmup, data-duration and data-concurrency must be positive")
    if not 0 <= args.stream_messages <= 100000:
        parser.error("stream-messages must be between 0 and 100000")
    if not 1 <= args.file_bytes <= 64 * 1024 * 1024:
        parser.error("file-bytes must be between 1 and 67108864")
    if not 1 <= args.dispatch_parallelism <= 1024:
        parser.error("dispatch-parallelism must be between 1 and 1024")
    args.publish_root = args.publish_root.resolve()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    variants = args.variants.split(",")
    binaries = {}
    dashboard_files = None
    for variant in variants:
        binary = args.publish_root / variant / "SlimFaas"
        dashboard = {}
        for asset in sorted((binary.parent / "wwwroot").rglob("*")):
            if asset.is_file():
                dashboard[str(asset.relative_to(binary.parent))] = hashlib.sha256(asset.read_bytes()).hexdigest()
        if dashboard_files is not None and dashboard != dashboard_files:
            parser.error("Dashboard assets differ between publications; use identical wwwroot contents in all variants")
        dashboard_files = dashboard
        files = [binary] + [binary.parent / name for name in
                           ["SlimFaas.dll", "SlimData.dll", "ZLinq.dll", "SlimFaas.runtimeconfig.json"]
                           if (binary.parent / name).exists()]
        hashes = {}
        for file in files:
            with file.open("rb") as source:
                hashes[file.name] = {"bytes": file.stat().st_size,
                                     "sha256": hashlib.file_digest(source, "sha256").hexdigest()}
        binaries[variant] = {"path": str(binary), "files": hashes, "dashboard_assets": dashboard}
    new_manifest = {
        "settings": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
        "platform": platform.platform(), "binaries": binaries,
        "candidate_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
        "candidate_diff_sha256": hashlib.sha256(subprocess.check_output(["git", "diff", "HEAD"], cwd=REPO)).hexdigest(),
        "sdk": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
        "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    }
    manifest_path = args.output / "manifest.json"
    if manifest_path.exists():
        if not args.resume:
            parser.error("Output already contains a campaign; use --resume to validate and continue it")
        previous = json.loads(manifest_path.read_text())
        for key, value in new_manifest["settings"].items():
            if key == "resume":
                continue
            old_value = previous["settings"].get(key, {"file_bytes": 262144, "dispatch_parallelism": 256}.get(key))
            if old_value != value:
                parser.error(f"Cannot resume: setting {key} differs")
        for variant in variants:
            if previous["binaries"][variant]["files"] != binaries[variant]["files"]:
                parser.error(f"Cannot resume: binary hashes differ for {variant}")
        suffix = str(time.time_ns())
        save(args.output / ("resume-manifest-" + suffix + ".json"), new_manifest)
        (args.output / ("harness-resumed-" + suffix + ".py")).write_bytes(Path(__file__).read_bytes())
    else:
        if args.resume:
            parser.error("Cannot resume: manifest.json does not exist")
        save(manifest_path, new_manifest)
        (args.output / "harness-as-run.py").write_bytes(Path(__file__).read_bytes())
    if args.dispatch_parallelism != 256:
        (args.output / "dispatch-overlay.yaml").write_text(
            "functions:\n  benchmark-latency:\n    annotations:\n"
            f'      SlimFaas/NumberParallelRequest: "{args.dispatch_parallelism}"\n'
            f'      SlimFaas/NumberParallelRequestPerPod: "{args.dispatch_parallelism}"\n')
    analysis_path = args.output / "analysis.json"
    analysis = json.loads(analysis_path.read_text()) if analysis_path.exists() else {}
    limits = analysis.get("rounds_by_variant", {})
    if set(limits) - set(variants) or any(type(count) is not int or not 1 <= count <= args.rounds
                                        for count in limits.values()):
        parser.error("Invalid explicit analysis repetition counts")
    if limits and not analysis.get("reason"):
        parser.error("Explicit analysis repetition counts require a reason")
    for mode in ["jit", "aot"]:
        before, after = "baseline-" + mode, "candidate-" + mode
        if before in variants and after in variants and limits.get(before, args.rounds) != limits.get(after, args.rounds):
            parser.error(f"Unbalanced before/after analysis counts: {mode}")
    for round_index in range(args.rounds):
        order = variants if round_index % 2 == 0 else list(reversed(variants))
        if round_index > 1:
            offset = round_index // 2
            order = order[offset:] + order[:offset]
        for variant in order:
            if round_index >= limits.get(variant, args.rounds):
                print(f"SKIP explicit analysis limit round={round_index + 1} variant={variant}", flush=True)
                continue
            directory = args.output / f"round-{round_index + 1}-{variant}"
            if args.resume and (directory / "complete.json").exists():
                print(f"SKIP completed round={round_index + 1} variant={variant}", flush=True)
                continue
            if args.resume and directory.exists():
                directory.rename(directory.with_name(directory.name + ".incomplete-" + str(time.time_ns())))
            benchmark(args, variant, round_index + 1)


if __name__ == "__main__":
    main()
