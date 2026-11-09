#!/usr/bin/env python3
"""Trace 2 MiB async acceptance, queue IDs and byte-verified delivery through local mode.

Diagnostic workload, not a replacement for the common .NET performance target.
It preserves the persistent cluster state and every receipt/error after 300 s drain.
"""
import argparse
import base64
import concurrent.futures
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import uuid

SPEC = importlib.util.spec_from_file_location("campaign", Path(__file__).with_name("slimfaas-zlinq-benchmark.py"))
CAMPAIGN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAMPAIGN)
RUNTIMES = {variant: CAMPAIGN.runtime_binary(CAMPAIGN.REPO / "artifacts" / "runtime", variant)
            for variant in ("baseline-jit", "candidate-jit", "baseline-aot", "candidate-aot")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--variant", choices=tuple(RUNTIMES), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--duration", type=int, default=20)
    parser.add_argument("--clients", type=int, default=16)
    args = parser.parse_args()
    runtime = RUNTIMES[args.variant]
    if not 1 <= args.duration <= 3600 or not 1 <= args.clients <= 256:
        parser.error("duration must be 1..3600 and clients 1..256")
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    body = bytes(index % 251 for index in range(2 * 1024 * 1024))
    expected_hash = hashlib.sha256(body).hexdigest()
    lock = threading.Lock()
    observations = {}
    deliveries = []
    samples = []
    file_observations = []
    errors = []
    finished = threading.Event()
    manifest = args.output / "local.yaml"
    manifest.write_text('''schemaVersion: 1
name: pr504-offload-trace
cluster:
  nodes: 3
  entrypointPort: 31020
  nodeHttpPortBase: 31021
  raftPortBase: 3162
  nodeLogLevel: Debug
  exposeLogs: true
state:
  mode: persistent
  directory: ''' + json.dumps(str(args.output / "state")) + '''
functions:
  benchmark-latency:
    debugUrl: http://127.0.0.1:31080
    annotations:
      SlimFaas/Function: "true"
      SlimFaas/NumberParallelRequest: "16"
      SlimFaas/NumberParallelRequestPerPod: "16"
      SlimFaas/ReplicasAtStart: "1"
      SlimFaas/ReplicasMin: "1"
    health:
      path: /health
      periodSeconds: 1
      timeoutSeconds: 2
      startupTimeoutSeconds: 120
''')

    class Target(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Length", "2")
            self.end_headers()
            self.wfile.write(b"OK")

        def do_POST(self):
            identifier = self.headers.get("X-Pr504-Request-Id", "")
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                chunks = []
                while True:
                    length = int(self.rfile.readline().split(b";", 1)[0], 16)
                    if length == 0:
                        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                            pass
                        break
                    chunks.append(self.rfile.read(length))
                    self.rfile.read(2)
                content = b"".join(chunks)
            else:
                content = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            row = {"request_id": identifier, "element_id": self.headers.get("SlimFaas-Element-Id"),
                   "time_ns": time.time_ns(), "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
            with lock:
                if row["bytes"] == len(body) and row["sha256"] == expected_hash:
                    observations[identifier] = observations.get(identifier, 0) + 1
                deliveries.append(row)
                with (args.output / "deliveries.jsonl").open("a") as journal:
                    journal.write(json.dumps(row) + "\n")
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()

    server = ThreadingHTTPServer(("127.0.0.1", 31080), Target)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    CAMPAIGN.save(args.output / "manifest.json", {
        "variant": args.variant, "runtime": str(runtime),
        "runtime_files": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                          for path in [runtime, *(runtime.parent / name for name in (
                              "SlimFaas.dll", "SlimData.dll", "ZLinq.dll", "SlimFaas.runtimeconfig.json", "SlimFaas.deps.json"))]
                          if path.exists()},
        "duration": args.duration, "clients": args.clients, "payload_bytes": len(body), "payload_sha256": expected_hash,
        "dispatch_capacity": 16, "drain_timeout_seconds": 300, "warmup_seconds": 0,
        "note": "Diagnostic target hashes/journals each body; do not compare its throughput to the .NET target.",
    })
    receipts = []
    process = None
    sampler = None
    try:
        with (args.output / "local.log").open("w") as log:
            subprocess.run([str(runtime), "local", "validate", "-f", str(manifest)],
                           cwd=CAMPAIGN.REPO, stdout=log, stderr=subprocess.STDOUT, check=True)
            process = CAMPAIGN.start_local([str(runtime), "local", "up", "-f", str(manifest)],
                                          cwd=CAMPAIGN.REPO, stdout=log, stderr=subprocess.STDOUT)
            ready_deadline = time.monotonic() + 180
            while True:
                if process.poll() is not None:
                    raise RuntimeError("Local supervisor exited during startup")
                try:
                    CAMPAIGN.get("http://127.0.0.1:31020/ready")
                    break
                except OSError:
                    if time.monotonic() >= ready_deadline:
                        raise TimeoutError("Cluster readiness exceeded 180 s") from None
                    time.sleep(.25)

            CAMPAIGN.record_processes(args.output, process, runtime.parent.parent)

            def sample():
                previous_files = set()
                while not finished.wait(.5):
                    try:
                        samples.append(dict(time_ns=time.time_ns(), **CAMPAIGN.resources()))
                        current_files = set()
                        for path in (args.output / "state").rglob("*.bin"):
                            encoded = path.name[:-4]
                            try:
                                identifier = base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4)).decode()
                            except (ValueError, UnicodeError):
                                continue
                            if not identifier.startswith("sf-internal-"):
                                continue
                            current_files.add((str(path.relative_to(args.output)), identifier))
                        now = time.time_ns()
                        file_observations.extend({"time_ns": now, "event": "appeared", "path": path, "file_id": identifier}
                                                 for path, identifier in current_files - previous_files)
                        file_observations.extend({"time_ns": now, "event": "disappeared", "path": path, "file_id": identifier}
                                                 for path, identifier in previous_files - current_files)
                        previous_files = current_files
                    except Exception as error:
                        errors.append({"stage": "metrics", "error": str(error)})

            sampler = threading.Thread(target=sample)
            sampler.start()
            deadline = time.monotonic() + args.duration

            def worker(_):
                rows = []
                while time.monotonic() < deadline:
                    identifier = uuid.uuid4().hex
                    row = {"request_id": identifier, "sent_ns": time.time_ns()}
                    try:
                        request = urllib.request.Request("http://127.0.0.1:31020/async-function/benchmark-latency/trace/" + identifier,
                            data=body, headers={"Content-Type": "application/octet-stream", "X-Pr504-Request-Id": identifier})
                        with CAMPAIGN.HTTP.open(request, timeout=60) as response:
                            row.update(status=response.status, element_id=response.headers.get("SlimFaas-Element-Id"))
                            response.read()
                        row["accepted_ns"] = time.time_ns()
                    except Exception as error:
                        row["error"] = str(error)
                    rows.append(row)
                    with lock:
                        with (args.output / "acceptances.jsonl").open("a") as journal:
                            journal.write(json.dumps(row) + "\n")
                return rows

            with concurrent.futures.ThreadPoolExecutor(max_workers=args.clients) as executor:
                receipts = [row for rows in executor.map(worker, range(args.clients)) for row in rows]
            accepted = {row["request_id"] for row in receipts if row.get("status") == 202}
            drain_deadline = time.monotonic() + 300
            while True:
                with lock:
                    missing = sorted(accepted - observations.keys())
                if not missing or time.monotonic() >= drain_deadline:
                    break
                time.sleep(.25)
            summary = {
                "accepted": len(accepted), "observed": len(accepted) - len(missing), "missing_ids": missing,
                "observed_without_202": sorted(observations.keys() - accepted),
                "duplicates": {key: value for key, value in observations.items() if value > 1},
                "body_mismatches": [row for row in deliveries if row["sha256"] != expected_hash or row["bytes"] != len(body)],
                "http_errors": [row for row in receipts if row.get("status") != 202], "sampling_errors": errors,
            }
            CAMPAIGN.save(args.output / "summary.json", summary)
            if missing or summary["duplicates"] or summary["body_mismatches"] or summary["http_errors"] or errors:
                raise RuntimeError("Async trace failed correctness checks; all receipts and persistent state are retained")
    finally:
        finished.set()
        if sampler is not None:
            sampler.join()
        try:
            if process is not None:
                CAMPAIGN.stop(process)
                CAMPAIGN.save(args.output / "shutdown.json", {"exit_code": process.returncode})
        finally:
            server.shutdown()
            server.server_close()
            CAMPAIGN.save(args.output / "resources.json", samples)
            CAMPAIGN.save(args.output / "file-observations.json", {"samples": file_observations,
                "poll_seconds": .5, "note": "Positive observations only; files with shorter lifetimes can be missed. Disappearance is not a timestamped delete syscall."})


if __name__ == "__main__":
    main()
