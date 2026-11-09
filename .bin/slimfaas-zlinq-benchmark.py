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
# The archived program is a fixed repository resource, never an HTTP response or
# a path supplied by a campaign manifest. Read it once before parsing CLI input.
HARNESS_SOURCE = (REPO / ".bin" / "slimfaas-zlinq-benchmark.py").read_bytes()
NODES = [f"http://127.0.0.1:{port}" for port in range(31021, 31024)]
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))
NODE_PIDS = []


def runtime_binary(root, variant):
    return root / variant / ("SlimFaas.exe" if os.name == "nt" else "SlimFaas")


def get(url):
    with HTTP.open(url, timeout=5) as response:
        return response.read().decode()


def io_counters():
    """Windows process I/O includes cached/device/network transfers, not just physical disk."""
    if os.name != "nt" or not NODE_PIDS:
        return None
    import ctypes
    from ctypes import wintypes

    class Counters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in (
            "read_operations", "write_operations", "other_operations", "read_bytes", "write_bytes", "other_bytes")]

    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    api.OpenProcess.restype = wintypes.HANDLE
    api.GetProcessIoCounters.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters)]
    api.GetProcessIoCounters.restype = wintypes.BOOL
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    api.CloseHandle.restype = wintypes.BOOL
    rows = []
    for pid in NODE_PIDS:
        handle = api.OpenProcess(0x1000, False, pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            counters = Counters()
            if not api.GetProcessIoCounters(handle, ctypes.byref(counters)):
                raise ctypes.WinError(ctypes.get_last_error())
            rows.append(dict(pid=pid, **{name: getattr(counters, name) for name, _ in Counters._fields_}))
        finally:
            api.CloseHandle(handle)
    return rows


def record_processes(directory, supervisor, publish_root):
    if os.name == "nt":
        processes = json.loads(subprocess.check_output(["powershell", "-NoProfile", "-Command",
            "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CommandLine | ConvertTo-Json"], text=True))
        selected = [row for row in processes if str(publish_root).lower() in (row.get("CommandLine") or "").lower()]
        save(directory / "processes.json", selected)
        NODE_PIDS[:] = [row["ProcessId"] for row in selected if row["ParentProcessId"] == supervisor.pid]
        if len(NODE_PIDS) != 3:
            raise RuntimeError(f"Expected three runtime nodes for I/O accounting; found {NODE_PIDS}")
    else:
        processes = subprocess.check_output(["ps", "-axo", "pid,ppid,command"], text=True)
        (directory / "processes.txt").write_text("\n".join(
            line for line in processes.splitlines() if str(publish_root) in line) + "\n")


def resources():
    def read_node(url):
        metrics = {}
        runtime = {}
        for line in get(url + "/metrics").splitlines():
            if line.startswith(("process_cpu_seconds_total ", "process_working_set_bytes ")):
                name, value = line.split()
                metrics[name] = float(value)
                if not math.isfinite(metrics[name]) or metrics[name] < 0:
                    raise ValueError(f"Invalid resource metric: {name}={value}")
            elif line.startswith(("dotnet_", "process_num_threads", "process_open_handles")):
                name, value = line.rsplit(" ", 1)
                runtime[name] = float(value)
        return metrics["process_cpu_seconds_total"], metrics["process_working_set_bytes"], runtime

    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
        nodes = list(executor.map(read_node, NODES))
    return {"cpu_seconds": sum(row[0] for row in nodes), "rss_bytes": sum(row[1] for row in nodes),
            "runtime_counters_by_node": [row[2] for row in nodes],
            "windows_io_by_node": io_counters(),
            "host_load_average": os.getloadavg() if hasattr(os, "getloadavg") else None}


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def archive_harness(destination):
    shutil.copyfile(REPO / ".bin" / "slimfaas-zlinq-benchmark.py", destination)


def run(command, log, **kwargs):
    print("RUN", " ".join(map(str, command)), flush=True)
    with log.open("w") as output:
        subprocess.run(list(map(str, command)), cwd=REPO, stdout=output,
                       stderr=subprocess.STDOUT, check=True, **kwargs)


def stop(process):
    if process.poll() is None:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            process.wait(timeout=20)
            return
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


def verified_files_case(args, directory):
    """Isolated cross-node cycles with changing content and no request retries."""
    directory.mkdir()
    failed_modes = []
    for mode in ("reused", "unique"):
        prefix = "verify-" + uuid.uuid4().hex
        before = resources()
        samples = [before]
        finished = threading.Event()
        sampling_errors = []
        started = time.monotonic()
        deadline = started + args.verified_file_duration

        def sample():
            try:
                while not finished.wait(0.5):
                    samples.append(resources())
            except Exception as error:
                sampling_errors.append(str(error))

        def worker(index):
            rows, errors = [], []
            sequence = 0
            while time.monotonic() < deadline:
                identifier = f"{prefix}-{index}" + (f"-{sequence}" if mode == "unique" else "")
                token = hashlib.sha256(f"{prefix}-{index}-{sequence}".encode()).digest()
                body = (token * ((args.file_bytes + len(token) - 1) // len(token)))[:args.file_bytes]
                upload_started = time.monotonic()
                try:
                    request = urllib.request.Request(NODES[index % 3] + f"/data/files?id={identifier}&ttl=120000",
                        data=body, headers={"Content-Type": "application/octet-stream"}, method="POST")
                    with HTTP.open(request, timeout=60) as response:
                        response.read()
                    uploaded = time.monotonic()
                    with HTTP.open(NODES[(index + 1) % 3] + f"/data/files/{identifier}", timeout=60) as response:
                        received = response.read()
                    downloaded = time.monotonic()
                    if received != body:
                        raise ValueError(f"Content mismatch: expected={hashlib.sha256(body).hexdigest()} "
                                         f"actual={hashlib.sha256(received).hexdigest()} bytes={len(received)}")
                    rows.append({"id": identifier, "sequence": sequence, "sha256": hashlib.sha256(body).hexdigest(),
                                 "upload_ms": (uploaded - upload_started) * 1000,
                                 "download_ms": (downloaded - uploaded) * 1000,
                                 "cycle_ms": (downloaded - upload_started) * 1000})
                except Exception as error:
                    errors.append({"id": identifier, "sequence": sequence, "error": str(error)})
                    break
                sequence += 1
            return rows, errors

        sampler = threading.Thread(target=sample)
        sampler.start()
        try:
            with concurrent.futures.ThreadPoolExecutor(max_workers=args.data_concurrency) as executor:
                results = list(executor.map(worker, range(args.data_concurrency)))
            elapsed = time.monotonic() - started
            after = resources()
        finally:
            finished.set()
            sampler.join()
        rows = [row for result in results for row in result[0]]
        errors = [error for result in results for error in result[1]]
        summary = {"mode": mode, "elapsed_seconds": elapsed, "completed": len(rows),
                   "rate": len(rows) / elapsed if elapsed else None, "errors": errors, "sampling_errors": sampling_errors,
                   "cpu_seconds": after["cpu_seconds"] - before["cpu_seconds"],
                   "peak_rss_bytes": max(item["rss_bytes"] for item in samples + [after])}
        for field in ("upload_ms", "download_ms", "cycle_ms"):
            values = sorted(row[field] for row in rows)
            summary[field] = {"p95": values[int(.95 * (len(values) - 1))] if values else None,
                              "p99": values[int(.99 * (len(values) - 1))] if values else None}
        save(directory / (mode + ".json"), {"summary": summary, "receipts": rows, "resources": samples})
        if errors or sampling_errors or not rows:
            failed_modes.append(mode)
    if failed_modes:
        raise RuntimeError(f"Verified file cases failed: {', '.join(failed_modes)}; see {directory}")


def benchmark(args, variant, round_number):
    NODE_PIDS.clear()
    binary = runtime_binary(args.publish_root, variant)
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
        record_processes(directory, process, args.publish_root)
        if not args.skip_http:
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
        if args.verified_file_duration:
            verified_files_case(args, directory / "verified-files")
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
    parser.add_argument("--skip-http", action="store_true", help="Run isolated data/file investigations without the HTTP matrix")
    parser.add_argument("--continue-after-failure", action="store_true",
                        help="Record failures and execute remaining planned attempts without retrying failed attempts")
    parser.add_argument("--verified-file-duration", type=int, default=0,
                        help="Seconds per reused/unique-ID file case; every GET is compared byte-for-byte")
    args = parser.parse_args()
    if min(args.rounds, args.duration, args.warmup, args.data_duration, args.data_concurrency) < 1:
        parser.error("rounds, duration, warmup, data-duration and data-concurrency must be positive")
    if not 0 <= args.stream_messages <= 100000:
        parser.error("stream-messages must be between 0 and 100000")
    if not 1 <= args.file_bytes <= 64 * 1024 * 1024:
        parser.error("file-bytes must be between 1 and 67108864")
    if not 1 <= args.dispatch_parallelism <= 1024:
        parser.error("dispatch-parallelism must be between 1 and 1024")
    if not 0 <= args.verified_file_duration <= 3600:
        parser.error("verified-file-duration must be between 0 and 3600")
    args.publish_root = args.publish_root.resolve()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    variants = args.variants.split(",")
    if len(set(variants)) != len(variants) or not set(variants) <= {
            "baseline-jit", "candidate-jit", "baseline-aot", "candidate-aot"}:
        parser.error("variants must be distinct baseline/candidate jit/aot names")
    binaries = {}
    dashboard_files = None
    for variant in variants:
        binary = runtime_binary(args.publish_root, variant)
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
        "harness_sha256": hashlib.sha256(HARNESS_SOURCE).hexdigest(),
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
        archive_harness(args.output / ("harness-resumed-" + suffix + ".py"))
    else:
        if args.resume:
            parser.error("Cannot resume: manifest.json does not exist")
        save(manifest_path, new_manifest)
        archive_harness(args.output / "harness-as-run.py")
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
    failures = []
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
            try:
                benchmark(args, variant, round_index + 1)
            except Exception as error:
                failure = {"variant": variant, "round": round_index + 1, "error": str(error)}
                failures.append(failure)
                save(directory / "failed.json", failure)
                if not args.continue_after_failure:
                    raise
    if failures:
        save(args.output / "failures.json", failures)
        raise RuntimeError(f"{len(failures)} planned attempts failed; no automatic retries were performed")


if __name__ == "__main__":
    main()
