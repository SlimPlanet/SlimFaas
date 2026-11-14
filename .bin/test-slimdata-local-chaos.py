#!/usr/bin/env python3
"""Exercise real SlimFaas Local APIs with three persistent nodes and owned faults.

Build SlimFaas Release first. All state, client intents/results, function receipts,
metrics and logs are retained in --output, including after failure. Mutations are
never retried. HTTP failures/timeouts during chaos remain unknown until reconciled.
"""

import argparse
import asyncio
from concurrent.futures import ThreadPoolExecutor
import ctypes
import errno
import hashlib
import http.client
import json
import os
from pathlib import Path
import platform
import re
import signal
import shutil
import socket
import subprocess
import sys
import threading
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlsplit
from urllib.request import build_opener, HTTPRedirectHandler, ProxyHandler, Request


ROOT = Path(__file__).resolve().parents[1]
SIZES = [65536, 262144, 1048576, 2000000, 2097152, 4194304]
HTTP = build_opener(ProxyHandler({}))
ENDPOINTS = {}
WEBSOCKET_ENDPOINTS = {}


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kwargs):
        return None


DIRECT_HTTP = build_opener(ProxyHandler({}), NoRedirect())
NETWORK_ERRORS = (URLError, TimeoutError, ConnectionError, http.client.HTTPException, OSError)


def sha256(value):
    return hashlib.sha256(value).hexdigest()


def payload(size, identifier=None):
    # Nonconstant bytes expose prefix, offset and truncation errors.
    pattern = bytes(range(256)) + b"SlimData-reliability\x00\xff"
    value = (pattern * ((size + len(pattern) - 1) // len(pattern)))[:size]
    if identifier:
        prefix = identifier.encode()[:size]
        value = prefix + value[len(prefix):]
    return value


def manifest_yaml(mapping, indent=0):
    """Emit block YAML without a Python YAML dependency (paths stay literal)."""
    def scalar(value):
        return "'" + value.replace("'", "''") + "'" if isinstance(value, str) else json.dumps(value)
    lines = []
    for key, value in mapping.items():
        prefix = " " * indent + key + ":"
        if isinstance(value, dict):
            lines.extend([prefix, manifest_yaml(value, indent + 2)])
        elif isinstance(value, list):
            lines.append(prefix)
            lines.extend(" " * (indent + 2) + "- " + scalar(item) for item in value)
        else:
            lines.append(prefix + " " + scalar(value))
    return "\n".join(lines)


def endpoint(port):
    return ENDPOINTS.get(port, f"http://127.0.0.1:{port}")


def request(port, path, method="GET", data=None, headers=None, timeout=8, chunked=False, redirects=True):
    body = iter(data[i:i + 65536] for i in range(0, len(data), 65536)) if chunked else data
    req = Request(endpoint(port) + path, data=body,
                  headers=headers or {}, method=method)
    try:
        with (HTTP if redirects else DIRECT_HTTP).open(req, timeout=timeout) as response:
            return response.status, dict(response.headers), response.read()
    except HTTPError as error:
        with error:
            return error.code, dict(error.headers), error.read()


def wait_until(description, predicate, timeout=180):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            if predicate():
                return
        except NETWORK_ERRORS as error:
            last = error
        time.sleep(min(.2, max(0, deadline - time.monotonic())))
    raise AssertionError(f"Timed out: {description}; last transport error: {last}")


def assert_minority_result(result, completed_at, restoration_at):
    status = result[0]
    if status is not None and 200 <= status < 300 and completed_at < restoration_at:
        raise AssertionError("Minority acknowledged a new write without quorum")


class Journal:
    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()

    def append(self, event):
        event = event | {"utc": time.time(), "monotonic": time.monotonic()}
        with self.lock, self.path.open("ab", buffering=0) as stream:
            stream.write((json.dumps(event) + "\n").encode())
            os.fsync(stream.fileno())


def read_receipts(directory):
    receipts = []
    for path in directory.glob("receipts-*.jsonl"):
        # A final partial line is still being written; only complete lines count.
        lines = path.read_bytes().splitlines(keepends=True)
        receipts.extend(json.loads(line) for line in lines if line.endswith(b"\n"))
    return sorted(receipts, key=lambda receipt: receipt.get("utc", 0))


def suspend(pid, paused):
    if os.name == "nt":
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x0800, False, pid)  # PROCESS_SUSPEND_RESUME
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            native = ctypes.WinDLL("ntdll")
            operation = native.NtSuspendProcess if paused else native.NtResumeProcess
            operation.argtypes = [wintypes.HANDLE]
            operation.restype = ctypes.c_long
            status = operation(handle)
            if status != 0:
                raise OSError(f"Process suspension failed: NTSTATUS {status:#x}")
        finally:
            kernel.CloseHandle(handle)
    else:
        os.kill(pid, signal.SIGSTOP if paused else signal.SIGCONT)


class Campaign:
    def __init__(self, options):
        self.options = options
        self.output = options.output.resolve()
        # Reusing a directory would mix accepted-operation ledgers from two runs.
        self.output.mkdir(parents=True, exist_ok=options.resume)
        self.state_root = (options.state_directory or self.output / "state").resolve()
        self.receipts = self.output / "receipts"
        self.receipts.mkdir(exist_ok=options.resume)
        self.client = Journal(self.output / "client.jsonl")
        self.entry = options.port_base
        self.ports = [self.entry + i + 1 for i in range(3)]
        self.raft = self.entry + 10
        self.runtime = options.runtime.resolve()
        self.command = [options.dotnet, str(self.runtime)] if self.runtime.suffix == ".dll" else [str(self.runtime)]
        self.process = None
        self.log = None
        self.suspended = set()
        self.expected = {}
        self.deleted = set()
        self.counter_results = []
        self.seeded = False
        self.results = []
        self.accepted = {}
        self.unknown = {}
        self.phase = "initialization"
        self.generation = 0
        if options.resume:
            self.restore_ledger()
        else:
            self.generate_manifest()

    def restore_ledger(self):
        parameters = json.loads((self.output / "parameters.json").read_text(encoding="utf-8"))
        if parameters["arguments"]["port_base"] != self.entry:
            raise ValueError("Resuming requires the original --port-base")
        self.manifest = self.output / "manifest.yaml"
        if not self.manifest.is_file() or not self.client.path.is_file():
            raise ValueError("Incomplete run: manifest and client ledger are required")
        report_path = self.output / "report.json"
        if report_path.is_file():
            old_report = json.loads(report_path.read_text(encoding="utf-8"))
            self.results = old_report["results"]
            (self.output / f"report-before-resume-{time.time_ns()}.json").write_bytes(report_path.read_bytes())
        self.generation = 1 + max((int(path.stem.split("-")[-1]) for path in self.output.glob("supervisor-*.log")), default=-1)
        for line in self.client.path.read_text(encoding="utf-8").splitlines():
            record = json.loads(line)
            if record["event"] == "unknown":
                self.unknown[record["id"]] = record
            if record["event"] != "accepted":
                continue
            self.restore_accepted(record)
        shutil.copyfile(__file__, self.output / f"campaign-resume-{self.generation}.py")

    def restore_accepted(self, record):
        record["response"] = record["response"].encode()
        self.accepted[record["id"]] = record
        kind = record["kind"]
        if kind in ("set", "minority-probe"):
            self.expected[record["path"]] = record["sha256"]
        elif kind in ("file", "hashset"):
            identifier = parse_qs(urlsplit(record["path"]).query)["id"][0]
            collection = "files" if kind == "file" else "hashsets"
            self.expected[f"/data/{collection}/{identifier}"] = record["sha256"]
        elif kind == "stream":
            receipt = json.loads(record["response"])
            self.expected[f"/data/streams/{receipt['Stream']}/messages/{receipt['MessageId']}/body"] = record["sha256"]
        elif kind == "counter":
            self.counter_results.append(int(record["response"]))
        elif kind == "counter-check" and urlsplit(record["path"]).path.endswith("/incrbyfloat"):
            # This final sequential operation follows both completed concurrent
            # phases. Retain its confirmed checkpoint when resuming the ledger.
            key = urlsplit(record["path"]).path.rsplit("/", 1)[0]
            self.expected[key] = sha256(record["response"])
        elif kind == "delete":
            self.expected.pop(record["path"], None)
            self.deleted.add(record["path"])

    def generate_manifest(self):
        fixture_source = ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py"
        fixture = self.output / "chaos_function.py"
        shutil.copyfile(fixture_source, fixture)
        shutil.copyfile(__file__, self.output / "campaign.py")
        annotations = {"SlimFaas/Function": "true", "SlimFaas/ReplicasMin": "1",
                       "SlimFaas/ReplicasAtStart": "1", "SlimFaas/NumberParallelRequest": "16",
                       "SlimFaas/NumberParallelRequestPerPod": "16",
                       "SlimFaas/SubscribeStream": "Public:chaos-stream",
                       "SlimFaas/StreamMaxLength": "100000", "SlimFaas/StreamMaxAge": "24h",
                       "SlimFaas/Configuration": json.dumps({"DefaultAsync": {
                           "HttpTimeout": 8, "TimeoutRetries": [1, 2],
                           "HttpStatusRetries": [500, 502, 503, 504],
                           "DeadLetterQueue": {"Enabled": True, "MaxAgeSeconds": 604800, "MaxLength": 100000}}})}
        manifest = {"schemaVersion": 1, "name": "slimdata-chaos",
                    "cluster": {"nodes": 3, "entrypointPort": self.entry,
                                "nodeHttpPortBase": self.ports[0], "raftPortBase": self.raft,
                                "nodeLogLevel": self.options.node_log_level},
                    "processPorts": {"from": self.entry + 30, "to": self.entry + 60},
                    "state": {"mode": "persistent", "directory": self.state_root.as_posix()},
                    "functions": {"chaos": {
                        "command": [sys.executable, str(fixture), "--journal-directory", str(self.receipts)],
                        "workingDirectory": str(ROOT), "annotations": annotations}},
                    "jobs": {"chaos-job": {
                        "command": [sys.executable, str(fixture), "--journal-directory", str(self.receipts), "--job"],
                        "workingDirectory": str(ROOT),
                        "annotations": {"SlimFaas/Job": "true", "SlimFaas/DefaultVisibility": "Public",
                                        "SlimFaas/NumberParallelJob": "2"},
                        "ttlSecondsAfterFinished": 3600, "backoffLimit": 1}}}
        wake_annotations = annotations | {"SlimFaas/ReplicasMin": "0", "SlimFaas/TimeoutSecondBeforeSetReplicasMin": "2"}
        wake_annotations.pop("SlimFaas/SubscribeStream")
        manifest["functions"]["chaos-wake"] = {
            "command": [sys.executable, str(fixture), "--journal-directory", str(self.receipts)],
            "workingDirectory": str(ROOT), "annotations": wake_annotations}
        if "streams" in self.options.scenarios.split(","):
            stream_annotations = annotations | {"SlimFaas/SubscribeStream": "Public:chaos-bounded",
                "SlimFaas/StreamMaxLength": "3", "SlimFaas/StreamMaxAge": "15s",
                "SlimFaas/Configuration": json.dumps({"DefaultAsync": {"HttpTimeout": 30,
                    "TimeoutRetries": [1, 2], "HttpStatusRetries": [500, 502, 503, 504]}})}
            for name in ("chaos-fanout-a", "chaos-fanout-b"):
                manifest["functions"][name] = {
                    "command": [sys.executable, str(fixture), "--journal-directory", str(self.receipts)],
                    "workingDirectory": str(ROOT), "annotations": stream_annotations,
                    "environment": {"CHAOS_FUNCTION": name}}
        self.manifest = self.output / "manifest.yaml"
        self.manifest.write_text(manifest_yaml(manifest) + "\n", encoding="utf-8")

    def start(self):
        for port in [self.entry] + self.ports + list(range(self.raft, self.raft + 3)):
            with socket.socket() as probe:
                probe.settimeout(.2)
                if probe.connect_ex(("127.0.0.1", port)) == 0:
                    raise RuntimeError(f"Port {port} already has a listener; stop the verified previous campaign before resuming its volumes")
        env = os.environ | {"SlimData__SnapshotIntervalEntries": "50",
                            "SlimData__SnapshotIntervalBytes": "8388608",
                            "OpenTelemetry__Enable": "false"}
        if "startup-jobs" in self.options.scenarios.split(","):
            env["Workers__JobsConfigurationDelayMilliseconds"] = "300000"
        if self.options.delivery_diagnostics:
            for category in ("WebSocketQueuesWorker", "WebSocketSendClient", "ClusterWebSocketSendClient"):
                env["Logging__LogLevel__SlimFaas.WebSocket." + category] = "Debug"
        creation = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
        self.log = (self.output / f"supervisor-{self.generation}.log").open("ab", buffering=0)
        self.process = subprocess.Popen(self.command + ["local", "up", "-f", str(self.manifest)],
                                        cwd=self.runtime.parent, env=env, stdout=self.log, stderr=subprocess.STDOUT, **creation)
        self.generation += 1
        self.ready()

    def ready(self):
        started = time.monotonic()
        def check():
            if self.process is not None and self.process.poll() is not None:
                raise AssertionError(f"Local supervisor exited with code {self.process.returncode}; inspect logs")
            return all(request(port, "/ready", timeout=2)[0] == 200 for port in self.ports)
        wait_until("all three nodes ready", check, self.options.recovery_timeout)
        wait_until("real function ready", lambda: request(self.entry, "/function/chaos/health", timeout=3)[0] == 200,
                   self.options.recovery_timeout)
        # A readiness transition can precede a usable Raft read barrier. Probe
        # reads only during startup; never retry a submitted mutation.
        missing = uuid.uuid4().hex
        wait_until("all node data read barriers available",
                   lambda: all(request(port, f"/data/sets/{missing}", timeout=2)[0] == 404 for port in self.ports),
                   self.options.recovery_timeout)
        self.client.append({"event": "cluster-ready", "phase": self.phase,
                            "supervisor_generation": self.generation - 1,
                            "seconds": time.monotonic() - started})

    def stop(self):
        try:
            while self.suspended:
                pid = next(iter(self.suspended))
                self.pause(pid, False)
                self.suspended.remove(pid)
        finally:
            try:
                self.stop_supervisor()
                wait_until("owned Local nodes release listening ports", self.ports_released, 30)
            finally:
                if self.log:
                    self.log.close()
                    self.log = None

    def stop_supervisor(self):
        if self.process is not None and self.process.poll() is None:
            self.process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
            try:
                self.process.wait(timeout=60)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"], check=True, capture_output=True)
                else:
                    os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=10)
                raise
    def ports_released(self):
        for port in self.ports + list(range(self.raft, self.raft + 3)):
            with socket.socket() as probe:
                probe.settimeout(.2)
                if probe.connect_ex(("127.0.0.1", port)) == 0:
                    return False
        return True

    def leader(self, excluded=()):
        for node in range(3):
            if node in excluded:
                continue
            try:
                status, headers, body = request(self.raft + node, "/SlimData/leader", timeout=.2, redirects=False)
                address = body if status == 200 else headers.get("Location", "").encode()
                match = re.search(rb"http://127\.0\.0\.1:(\d+)", address)  # NOSONAR - Local Raft discovery is loopback-only and has no TLS listener.
                if status in (200, 301, 302, 307, 308) and match:
                    leader = int(match[1]) - self.raft
                    if leader not in excluded and 0 <= leader < 3:
                        return leader
            except NETWORK_ERRORS:
                pass
        return None

    def pause(self, pid, paused):
        suspend(pid, paused)

    def kill_node(self, pid):
        os.kill(pid, signal.SIGTERM if os.name == "nt" else signal.SIGKILL)

    def node_pid(self, node):
        # Optional logs live on the faulted filesystem and can miss a restart
        # while it is full. The supervisor's captured stdout lives with the
        # independent client ledger, outside that volume.
        log = self.output / f"supervisor-{self.generation - 1}.log"
        matches = re.findall(rf"\[node/slimfaas-{node}\] started pid=(\d+)",
                             log.read_text(encoding="utf-8", errors="replace"))
        # macOS launches the runtime behind the system wait implementation so
        # stopping it does not block .NET's supervisor-wide child reaper.
        runtime_matches = re.findall(rf"\[node/slimfaas-{node}\] runtime pid=(\d+)",
                                     log.read_text(encoding="utf-8", errors="replace"))
        if runtime_matches:
            matches = runtime_matches
        if not matches:
            raise AssertionError(f"Owned node PID missing: {log}")
        return int(matches[-1])

    def mutate(self, kind, path, body, identifier=None, headers=None, chunked=False, port=None, timeout=None, method="POST"):
        identifier = identifier or uuid.uuid4().hex
        record = {"id": identifier, "kind": kind, "path": path, "bytes": len(body),
                  "sha256": sha256(body), "phase": self.phase, "port": port or self.entry,
                  "chunked": chunked, "method": method}
        if kind == "job":
            record["job_args"] = json.loads(body)["Args"]
        self.client.append(record | {"event": "intent"})
        started = time.monotonic()
        try:
            status, response_headers, response = request(port or self.entry, path, method, body,
                {"Content-Type": "application/octet-stream", "X-Chaos-Id": identifier,
                 "X-Chaos-Sha256": record["sha256"]} | (headers or {}),
                timeout=timeout or self.options.request_timeout, chunked=chunked)
        except NETWORK_ERRORS as error:
            self.unknown[identifier] = record
            self.client.append(record | {"event": "unknown", "error": repr(error),
                                         "seconds": time.monotonic() - started})
            return None, None, None, identifier
        # 5xx may occur after commit. Treat them as unknown, not proven rejection.
        result = "rejected"
        if 200 <= status < 300:
            result = "accepted"
        elif status >= 500:
            result = "unknown"
        self.client.append(record | {"event": result, "status": status,
                                     "headers": response_headers, "response": response[:4096].decode(errors="replace"),
                                     "seconds": time.monotonic() - started})
        if result == "accepted":
            self.accept_result(record, response, response_headers)
        elif result == "unknown":
            self.unknown[identifier] = record
        return status, response_headers, response, identifier

    def accept_result(self, record, response, headers):
        self.accepted[record["id"]] = record | {"response": response, "headers": headers}
        if record["kind"] == "minority-probe":
            self.expected[record["path"]] = record["sha256"]
        if record["kind"] == "counter":
            self.counter_results.append(int(response))

    def require_mutation(self, kind, path, body, expected_status=None, **kwargs):
        status, _, response, identifier = self.mutate(kind, path, body, **kwargs)
        if status is None or not 200 <= status < 300 or (expected_status and status != expected_status):
            raise AssertionError(f"{kind} {identifier}: HTTP {status}, {response!r}")
        return response, identifier

    def observe(self, identifier, timeout=None, minimum_successes=1):
        def check():
            rows = [r for r in read_receipts(self.receipts) if r.get("id") == identifier and r["event"] == "completed"]
            for row in rows:
                if row["sha256"] != row["expected_sha256"]:
                    raise AssertionError(f"Function checksum mismatch: {row}")
            return sum(200 <= r["status"] < 300 for r in rows) >= minimum_successes
        wait_until(f"function receipt {identifier}", check, timeout or self.options.recovery_timeout)

    def capture(self, name):
        for node, port in enumerate(self.ports):
            try:
                status, _, body = request(port, "/metrics", timeout=3)
                (self.output / f"{name}-node-{node}.prom").write_bytes(body)
                self.client.append({"event": "metrics", "phase": name, "node": node, "status": status})
                if status == 200:
                    self.record_metrics(name, node, body)
            except NETWORK_ERRORS as error:
                self.client.append({"event": "metrics-unavailable", "phase": name, "node": node, "error": repr(error)})

    def record_metrics(self, phase, node, body):
        values = {}
        for line in body.decode().splitlines():
            if line.startswith(("slimdata_", "process_", "system_runtime_")):
                parts = line.rsplit(" ", 1)
                if len(parts) == 2:
                    values[parts[0]] = float(parts[1])
        for key, value in values.items():
            if key.startswith(("slimdata_raft_command_size_violation_total", "slimdata_raft_command_format_violation_total",
                               "slimdata_raft_skipped_command_total")) and value > 0:
                raise AssertionError(f"Raft command was skipped or rejected while applying: {key}={value}")
        root = self.node_state_directory(node)
        snapshots = [{"name": path.name, "bytes": path.stat().st_size,
                      "mtime": path.stat().st_mtime} for path in (root / "db").glob("*") if path.is_file()]
        wal_bytes = sum(path.stat().st_size for path in (root / "wal").rglob("*") if path.is_file())
        self.client.append({"event": "resource-sample", "phase": phase, "node": node,
                            "metrics": values, "snapshots": snapshots, "wal_disk_bytes": wal_bytes})

    def node_state_directory(self, node):
        return self.state_root / "data" / f"slimfaas-{node}"

    def verify(self):
        # All reads share a deadline; 404 is allowed only while catching up. Wrong
        # values and server errors fail immediately, never becoming a retry pass.
        deadline = time.monotonic() + self.options.recovery_timeout
        for path, expected in self.expected.items():
            self.verify_value(path, expected, deadline)
        for path in self.deleted:
            for port in self.ports:
                status, _, _ = request(port, path, timeout=5)
                if status != 404:
                    raise AssertionError(f"Confirmed deletion is visible on {port}{path}: HTTP {status}")
        self.verify_counters(deadline)

    def verify_value(self, path, expected, deadline):
        for port in self.ports:
            def check(port=port):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AssertionError("Convergence verification exceeded phase deadline")
                status, _, body = request(port, path, timeout=min(5, remaining))
                if status == 404:
                    return False
                if status != 200 or sha256(body) != expected:
                    raise AssertionError(f"Convergence failure at {port}{path}: HTTP {status}, sha256={sha256(body)}")
                return True
            wait_until(f"node {port} {path}", check, max(.001, deadline - time.monotonic()))

    def verify_counters(self, deadline=None):
        unknown_counters = sum(record["kind"] == "counter" for record in self.unknown.values())
        if not self.counter_results and not unknown_counters:
            return
        # Unknown responses may have committed; never retry the mutation.
        confirmed = len(self.counter_results)
        lower = max(confirmed, max(self.counter_results, default=0))
        upper = confirmed + unknown_counters
        deadline = deadline if deadline is not None else time.monotonic() + self.options.recovery_timeout
        previous = None
        def converged():
            nonlocal previous
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError("Mixed counter convergence exceeded phase deadline")
            values = []
            for port in self.ports:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AssertionError("Mixed counter convergence exceeded phase deadline")
                values.append(request(port, "/data/sets/chaos-mixed-counter", timeout=min(5, remaining)))
            if lower == 0 and all(status == 404 for status, _, _ in values):
                return True
            if any(status != 200 for status, _, _ in values):
                raise AssertionError("Mixed counter is unavailable after recovery")
            numbers = [int(value) for _, _, value in values]
            self.client.append({"event": "counter-observation", "phase": self.phase,
                                "values": numbers, "confirmed": confirmed, "minimum": lower, "possible": upper})
            if (len(set(self.counter_results)) != confirmed or any(not lower <= number <= upper for number in numbers)
                    or previous is not None and any(now < before for now, before in zip(numbers, previous))):
                raise AssertionError(f"Mixed counter invariant: {numbers}, confirmed={lower}, possible={upper}")
            previous = numbers
            if len(set(numbers)) == 1:
                return True
            if not unknown_counters:
                raise AssertionError(f"Mixed counter invariant without outstanding unknown writes: {numbers}")
            # A timed-out increment can commit between these sequential reads.
            # Observe convergence within the existing phase budget; never submit
            # the increment again or excuse an invalid/regressing value.
            return False
        wait_until("mixed counter convergence", converged, max(.001, deadline - time.monotonic()))

    def payloads(self):
        self.phase = "payloads"
        cases = [(size, chunked) for size in self.options.payload_sizes for chunked in (False, True)]
        def exercise(case):
            size, chunked = case
            identifier = uuid.uuid4().hex
            body = payload(size, identifier)
            started = time.monotonic()
            _, identifier = self.require_mutation("async", "/async-function/chaos/consume", body,
                                                 expected_status=202, chunked=chunked, identifier=identifier)
            self.observe(identifier)
            result = {"phase": "payloads", "bytes": size, "chunked": chunked,
                      "id": identifier, "seconds": time.monotonic() - started, "passed": True}
            self.results.append(result)
            print(json.dumps(result), flush=True)
        with ThreadPoolExecutor(max_workers=self.options.concurrency) as pool:
            list(pool.map(exercise, cases))
        self.capture("payloads")

    def api_smoke(self):
        self.phase = "api-smoke"
        value = payload(65536)
        for kind, post, get in [("set", "/data/sets/chaos-value", "/data/sets/chaos-value"),
                                ("hashset", "/data/hashsets?id=chaos-hash", "/data/hashsets/chaos-hash"),
                                ("file", "/data/files?id=chaos-file", "/data/files/chaos-file")]:
            self.require_mutation(kind, post, value)
            self.expected[get] = sha256(value)
        response, _ = self.require_mutation("sync", "/function/chaos/echo?status=201", value, expected_status=201)
        if json.loads(response)["sha256"] != sha256(value):
            raise AssertionError("Sync checksum mismatch")
        response, identifier = self.require_mutation("stream", "/publish-stream/chaos-stream/consume", value,
                                                    expected_status=202)
        message = json.loads(response)["MessageId"]
        self.expected[f"/data/streams/chaos-stream/messages/{message}/body"] = sha256(value)
        self.observe(identifier)
        self.counter_contracts()
        self.require_mutation("ttl", "/data/sets/chaos-ttl?ttl=1000", b"expires")
        wait_until("set TTL", lambda: request(self.entry, "/data/sets/chaos-ttl")[0] == 404)
        self.require_mutation("ttl", "/data/hashsets?id=chaos-hash-ttl&ttl=1000", b"expires")
        self.require_mutation("ttl", "/data/files?id=chaos-file-ttl&ttl=1000", b"expires")
        for collection, identifier in (("hashsets", "chaos-hash-ttl"), ("files", "chaos-file-ttl")):
            wait_until(f"{collection} TTL", lambda collection=collection, identifier=identifier:
                       request(self.entry, f"/data/{collection}/{identifier}")[0] == 404)
        for name in ("sets", "hashsets"):
            status, _, _ = request(self.entry, f"/data/{name}" + ("?id=oversize" if name == "hashsets" else "/oversize"),
                                   "POST", payload(1048577))
            if status != 413:
                raise AssertionError(f"{name} over-limit rejection: {status}")
        for collection in ("sets", "hashsets", "files"):
            identifier = "delete-" + uuid.uuid4().hex
            path = f"/data/{collection}/{identifier}"
            post = f"/data/{collection}?id={identifier}" if collection != "sets" else path
            self.require_mutation("delete-fixture", post, value)
            self.require_mutation("delete", path, b"", method="DELETE")
            self.deleted.add(path)
        self.verify()
        self.results.append({"phase": self.phase, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def counter_contracts(self):
        key = "/data/sets/chaos-counter-" + uuid.uuid4().hex
        with ThreadPoolExecutor(max_workers=self.options.concurrency) as pool:
            increments = list(pool.map(lambda _: self.require_mutation("counter-check", key + "/incr", b"")[0], range(32)))
        if sorted(int(x) for x in increments) != list(range(1, 33)):
            raise AssertionError(f"Nonatomic counter results: {increments}")
        with ThreadPoolExecutor(max_workers=self.options.concurrency) as pool:
            decrements = list(pool.map(lambda _: self.require_mutation("counter-check", key + "/decr", b"")[0], range(32)))
        if sorted(int(x) for x in decrements) != list(range(32)):
            raise AssertionError(f"Nonatomic decrement results: {decrements}")
        for operation, result in (("incrby?by=42", b"42"), ("decrby?by=10", b"32"),
                                  ("incrbyfloat?by=0.5", b"32.5")):
            actual, _ = self.require_mutation("counter-check", f"{key}/{operation}", b"")
            if actual != result:
                raise AssertionError(f"Counter {operation}: {actual!r}, expected {result!r}")
        self.expected[key] = sha256(b"32.5")

    def retries_dlq_jobs(self):
        self.phase = "retries-dlq-jobs"
        value = payload(2097152)
        _, identifier = self.require_mutation("async", "/async-function/chaos/consume?fail=2", value, expected_status=202)
        self.observe(identifier)
        attempts = [r for r in read_receipts(self.receipts) if r.get("id") == identifier and r["event"] == "completed"]
        if not {0, 1, 2}.issubset({r["attempt"] for r in attempts}):
            raise AssertionError(f"Missing retry attempts: {attempts}")
        if [(r["attempt"], r["last_try"]) for r in attempts] != [(0, "false"), (1, "false"), (2, "true")]:
            raise AssertionError(f"Incorrect attempt/final delivery metadata: {attempts}")
        _, failed_id = self.require_mutation("dlq", "/async-function/chaos/consume?fail=always", value, expected_status=202)
        element_id = next(v for k, v in self.accepted[failed_id]["headers"].items() if k.lower() == "slimfaas-element-id")
        dead_letter = {}
        def retained():
            nonlocal dead_letter
            status, _, body = request(self.entry, "/data/dead-letters/chaos/messages")
            if status != 200:
                return False
            dead_letter = next((r for r in json.loads(body)["Items"] if r["OriginalElementId"] == element_id), {})
            return bool(dead_letter)
        wait_until("terminal failure retained in DLQ", retained, self.options.recovery_timeout)
        dead_path = f"/data/dead-letters/chaos/messages/{dead_letter['Id']}"
        status, _, body = request(self.entry, dead_path + "/body")
        if status != 200 or sha256(body) != sha256(value):
            raise AssertionError("DLQ offloaded body integrity failure")
        (self.receipts / "repair").write_text("fixed", encoding="utf-8")
        self.require_mutation("replay", dead_path + "/requeue", b"")
        self.observe(failed_id)
        job_ids = [uuid.uuid4().hex for _ in range(3)]
        for job_id in job_ids:
            self.require_mutation("job", "/job/chaos-job", json.dumps({"Args": [job_id], "TtlSecondsAfterFinished": 3600}).encode(),
                                  headers={"Content-Type": "application/json"}, expected_status=202)
        wait_until("real job processes", lambda: set(job_ids).issubset(
            {arg for row in read_receipts(self.receipts) if row["event"] == "job" for arg in row["args"]}),
            self.options.recovery_timeout)
        self.results.append({"phase": self.phase, "passed": True, "job_ids": job_ids, "dlq_id": dead_letter["Id"]})
        print(json.dumps(self.results[-1]), flush=True)

    def job_during_node_stop(self):
        self.phase = "job-during-node-stop"
        leader = self.leader()
        if leader is None:
            raise AssertionError("A leader is required before the follower suspension")
        follower = (leader + 1) % 3
        pid = self.node_pid(follower)
        identifier = uuid.uuid4().hex
        self.pause(pid, True)
        self.suspended.add(pid)
        try:
            started = time.monotonic()
            self.require_mutation("job", "/job/chaos-job",
                json.dumps({"Args": [identifier], "TtlSecondsAfterFinished": 3600}).encode(),
                port=self.ports[leader], headers={"Content-Type": "application/json"}, expected_status=202)
            wait_until("real job while follower is still stopped", lambda: any(
                identifier in row.get("args", []) for row in read_receipts(self.receipts) if row["event"] == "job"),
                min(15, self.options.recovery_timeout))
            self.results.append({"phase": self.phase, "passed": True, "follower": follower,
                                 "job_id": identifier, "seconds": time.monotonic() - started})
            print(json.dumps(self.results[-1]), flush=True)
        finally:
            self.pause(pid, False)
            self.suspended.discard(pid)
        self.ready()

    def protocol_timeout(self):
        self.phase = "protocol-probe-with-silent-follower"
        leader = self.leader()
        if leader is None:
            raise AssertionError("No leader before protocol-probe regression")
        follower = (leader + 1) % 3
        stopped = self.node_pid(follower)
        previous = self.node_pid(leader)
        self.pause(stopped, True)
        self.suspended.add(stopped)
        try:
            crashed_at = time.monotonic()
            self.kill_node(previous)
            self.client.append({"event": "leader-crashed-with-silent-follower", "node": leader,
                                "pid": previous, "follower": follower})
            wait_until("owned leader process restarts", lambda: self.node_pid(leader) != previous,
                       self.options.recovery_timeout)
            wait_until("restarted runtime opens its HTTP listener", lambda:
                request(self.ports[leader], "/health", timeout=1)[0] == 200,
                self.options.recovery_timeout)
            started = time.monotonic()
            wait_until("remaining majority becomes ready despite a silent protocol endpoint", lambda:
                all(request(port, "/ready", timeout=1)[0] == 200
                    for index, port in enumerate(self.ports) if index != follower), 15)
            path = "/data/sets/protocol-probe-" + uuid.uuid4().hex
            current = self.leader(excluded=(follower,))
            if current is None:
                raise AssertionError("No leader in the remaining majority")
            self.require_mutation("set", path, b"healthy-majority", port=self.ports[current],
                                  expected_status=200, timeout=15)
            self.expected[path] = sha256(b"healthy-majority")
            self.results.append({"phase": self.phase, "passed": True, "suspended_follower": follower,
                                 "restarted_leader": leader, "ready_and_write_seconds": time.monotonic() - started})
            self.results[-1]["restart_to_listener_seconds"] = started - crashed_at
            print(json.dumps(self.results[-1]), flush=True)
        finally:
            self.pause(stopped, False)
            self.suspended.discard(stopped)
        self.ready()
        self.verify()

    def schedules(self):
        self.phase = "scheduled-job"
        identifier = uuid.uuid4().hex
        value = json.dumps({"Schedule": "* * * * *", "TimeZone": "UTC", "Args": [identifier],
                            "TtlSecondsAfterFinished": 3600}).encode()
        receipt, _ = self.require_mutation("schedule", "/job-schedules/chaos-job", value,
            headers={"Content-Type": "application/json"}, expected_status=201)
        schedule_id = json.loads(receipt)["Id"]
        def visible():
            for port in self.ports:
                status, _, body = request(port, "/job-schedules/chaos-job")
                if status != 200 or not any(row["Id"] == schedule_id for row in json.loads(body)):
                    return False
            return True
        wait_until("schedule replicated to every node", visible, self.options.recovery_timeout)
        self.stop()
        self.start()
        if not visible():
            raise AssertionError("Accepted schedule missing after persistent restart")
        wait_until("cron launched a real job process", lambda: any(
            identifier in row.get("args", []) for row in read_receipts(self.receipts) if row["event"] == "job"),
            self.options.recovery_timeout)
        path = f"/job-schedules/chaos-job/{schedule_id}"
        self.require_mutation("schedule-delete", path, b"", method="DELETE", expected_status=204)
        for port in self.ports:
            status, _, body = request(port, "/job-schedules/chaos-job")
            self.client.append({"event": "schedule-deletion-read", "port": port, "status": status,
                                "body": body.decode(errors="replace"), "schedule_id": schedule_id})
            if status != 200 or any(row["Id"] == schedule_id for row in json.loads(body)):
                raise AssertionError(f"Deleted schedule read on {port}: HTTP {status}, body={body!r}")
        self.results.append({"phase": self.phase, "schedule_id": schedule_id, "job_arg": identifier, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def stream_contracts(self):
        self.phase = "stream-fanout-retention"
        stream = "/data/streams/chaos-bounded"
        functions = ("chaos-fanout-a", "chaos-fanout-b")
        for function in functions:
            wait_until("independent Stream subscription " + function,
                lambda function=function: request(self.entry, f"{stream}/functions/{function}")[0] == 200,
                self.options.recovery_timeout)
        def publish(delay=0):
            identifier = uuid.uuid4().hex
            receipt, _ = self.require_mutation("fanout", f"/publish-stream/chaos-bounded/consume?delay={delay}",
                payload(65536, identifier), identifier=identifier, expected_status=202)
            return identifier, json.loads(receipt)["MessageId"]
        def completed(identifiers):
            rows = read_receipts(self.receipts)
            return all(any(row.get("function") == function and row.get("id") == identifier and
                           self.receipt_succeeded(row) for row in rows)
                       for identifier in identifiers for function in functions)
        def acknowledged():
            return all(json.loads(request(self.entry, f"{stream}/functions/{function}")[2])["Lag"] == 0
                       for function in functions)
        with ThreadPoolExecutor(max_workers=self.options.concurrency) as pool:
            initial = list(pool.map(lambda _: publish(), range(3)))
        wait_until("both independent functions processed every publication",
                   lambda: completed([identifier for identifier, _ in initial]), self.options.recovery_timeout)
        wait_until("both independent contiguous ACK positions", acknowledged, self.options.recovery_timeout)
        history = self.stream_history(stream)
        if history != sorted(message for _, message in initial):
            raise AssertionError(f"Retained append order changed across pages: {history}")
        for _ in range(3):
            identifier, _ = publish()
            wait_until("both consumers process before further retention pressure",
                       lambda identifier=identifier: completed([identifier]), self.options.recovery_timeout)
            wait_until("both consumers ACK before further retention pressure", acknowledged, self.options.recovery_timeout)
        self.expired_stream_cursor(stream, history[0])
        delayed, message = publish(20)
        wait_until("retention expires an in-flight publication", lambda:
            request(self.entry, stream + f"/messages/{message}/body")[0] == 410, self.options.recovery_timeout)
        gaps = self.stream_retention_gaps(stream, functions)
        wait_until("both late real handlers finish", lambda: completed([delayed]), self.options.recovery_timeout)
        self.stop()
        self.start()
        identifier, _ = publish()
        wait_until("both functions resume after full persistent restart",
                   lambda: completed([identifier]), self.options.recovery_timeout)
        self.results.append({"phase": self.phase, "passed": True, "initial_append_order": history,
                             "retention_gap_summaries": gaps, "functions": functions})
        print(json.dumps(self.results[-1]), flush=True)

    def stream_history(self, stream):
        history = []
        after = ""
        while True:
            status, _, body = request(self.entry, stream + "?limit=1" + ("&after=" + after if after else ""))
            if status != 200:
                raise AssertionError(f"Stream history page: HTTP {status}, {body!r}")
            page = json.loads(body)
            history.extend(row["Id"] for row in page["Items"])
            after = page["NextCursor"]
            if not after:
                break
        return history

    def expired_stream_cursor(self, stream, cursor):
        for port in self.ports:
            status, _, body = request(port, stream + "?after=" + cursor)
            if status != 410 or "EarliestMessageId" not in json.loads(body):
                raise AssertionError(f"Expired history cursor must report boundary: HTTP {status}, {body!r}")

    def stream_retention_gaps(self, stream, functions):
        gaps = {}
        for function in functions:
            status, _, body = request(self.entry, f"{stream}/functions/{function}")
            summary = json.loads(body)
            if status != 200 or summary["RetentionGapCount"] < 1:
                raise AssertionError(f"Expired unacknowledged Stream message was not counted: {body!r}")
            gaps[function] = summary
        return gaps

    def wake_and_cancel(self):
        self.phase = "wake-and-cancel"
        def replicas():
            status, _, body = request(self.entry, "/status-functions")
            if status != 200:
                raise AssertionError(f"Function status returned HTTP {status}")
            return next(row["NumberReady"] for row in json.loads(body) if row["Name"] == "chaos-wake")
        wait_until("function starts scaled to zero", lambda: replicas() == 0, self.options.recovery_timeout)
        value = payload(65536)
        response, _ = self.require_mutation("sync", "/function/chaos-wake/echo", value)
        if json.loads(response)["sha256"] != sha256(value):
            raise AssertionError("Wake-from-zero changed the payload")
        wait_until("function scales back to zero", lambda: replicas() == 0, self.options.recovery_timeout)
        status, _, _, identifier = self.mutate("sync", "/function/chaos/echo?delay=1", value, timeout=.05)
        if status is not None:
            raise AssertionError("Expected the controlled sync request to time out")
        # The response is unknown, even if the handler completes later.
        self.observe(identifier)
        self.results.append({"phase": self.phase, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def file_boundaries(self):
        self.phase = "file-boundaries"
        for size in (0, 1048576, 4194304, 16777216):
            identifier = uuid.uuid4().hex
            value = payload(size, identifier)
            self.require_mutation("file", f"/data/files?id={identifier}", value, identifier=identifier)
            self.expected[f"/data/files/{identifier}"] = sha256(value)
            self.verify()
            print(json.dumps({"phase": self.phase, "bytes": size, "passed": True}), flush=True)
        identifier = uuid.uuid4().hex
        self.client.append({"event": "intent", "kind": "interrupted-upload", "id": identifier})
        address = urlsplit(endpoint(self.entry))
        with socket.create_connection((address.hostname, address.port or 80), timeout=3) as connection:
            connection.sendall((f"POST /data/files?id={identifier} HTTP/1.1\r\nHost: localhost\r\n"
                                "Content-Type: application/octet-stream\r\nContent-Length: 1048576\r\n\r\n").encode() + payload(65536))
            connection.shutdown(socket.SHUT_WR)
        # A successful subsequent write provides a barrier after the failed
        # transfer. The partial ID must not become an acknowledged file.
        self.require_mutation("set", f"/data/sets/barrier-{identifier}", b"barrier")
        for port in self.ports:
            status, _, _ = request(port, f"/data/files/{identifier}")
            if status != 404:
                raise AssertionError(f"Interrupted upload visible on {port}: HTTP {status}")
        self.results.append({"phase": self.phase, "passed": True})

    def reconcile(self):
        async_ids = {identifier for identifier, record in self.accepted.items() if record["kind"] in ("async", "stream")}
        job_args = {arg for record in self.accepted.values() if record["kind"] == "job" for arg in record["job_args"]}
        def check():
            rows = read_receipts(self.receipts)
            completed = {row["id"] for row in rows if self.receipt_succeeded(row)}
            missing = async_ids - completed
            missing_jobs = job_args - {arg for row in rows if row["event"] == "job" for arg in row["args"]}
            self.missing = sorted(missing | missing_jobs)
            return not self.missing
        wait_until("all confirmed async/stream publications executed: see missing IDs in report", check,
                   self.options.recovery_timeout)

    def receipt_succeeded(self, row):
        if row.get("event") != "completed":
            return False
        if row["sha256"] != row["expected_sha256"]:
            raise AssertionError(f"Function received a corrupt body: {row}")
        expected = self.accepted.get(row.get("id"))
        if expected is None:
            return False
        if row["sha256"] != expected["sha256"] or row["bytes"] != expected["bytes"]:
            raise AssertionError(f"Receipt integrity failure: {row}")
        return 200 <= row["status"] < 300

    def traffic(self, stopping):
        """One independent producer per API: mixed operations overlap during faults."""
        with ThreadPoolExecutor(max_workers=8) as pool:
            futures = [pool.submit(self.produce, kind, stopping) for kind in ("async", "sync", "stream", "file", "hashset", "set", "counter", "job")]
            # Propagate producer failures promptly; their exceptions must never be
            # hidden in a background task while the campaign reports success.
            try:
                while not stopping.wait(.2):
                    for future in futures:
                        if future.done():
                            future.result()
            finally:
                stopping.set()
                for future in futures:
                    future.result()

    def traffic_request(self, kind, identifier, sequence):
        value = payload(1024, identifier)
        if kind == "async":
            value = payload(self.options.payload_sizes[sequence % len(self.options.payload_sizes)], identifier)
            return "/async-function/chaos/consume?delay=0.1", value, None, None
        if kind == "job":
            value = json.dumps({"Args": [identifier], "TtlSecondsAfterFinished": 3600}).encode()
            return "/job/chaos-job", value, None, {"Content-Type": "application/json"}
        routes = {"sync": ("/function/chaos/echo?delay=0.1", None),
                  "stream": ("/publish-stream/chaos-stream/consume", None),
                  "file": (f"/data/files?id={identifier}", f"/data/files/{identifier}"),
                  "hashset": (f"/data/hashsets?id={identifier}", f"/data/hashsets/{identifier}"),
                  "set": (f"/data/sets/{identifier}", f"/data/sets/{identifier}"),
                  "counter": ("/data/sets/chaos-mixed-counter/incr", None)}
        path, get = routes[kind]
        return path, b"" if kind == "counter" else value, get, None

    def produce(self, kind, stopping):
        sequence = 0
        while not stopping.is_set():
            identifier = uuid.uuid4().hex
            path, value, get, headers = self.traffic_request(kind, identifier, sequence)
            status, _, response, _ = self.mutate(kind, path, value, identifier=identifier, headers=headers)
            if status is not None and 200 <= status < 300:
                self.check_traffic_result(kind, value, get, response)
            sequence += 1
            stopping.wait(self.options.traffic_interval)

    def check_traffic_result(self, kind, value, get, response):
        if get:
            self.expected[get] = sha256(value)
        if kind == "stream":
            receipt = json.loads(response)
            self.expected[f"/data/streams/{receipt['Stream']}/messages/{receipt['MessageId']}/body"] = sha256(value)
        if kind == "sync" and json.loads(response)["sha256"] != sha256(value):
            raise AssertionError("Sync corruption during chaos")

    def audit(self):
        """Account for duplicate deliveries and commit/response ambiguity explicitly."""
        rows = read_receipts(self.receipts)
        completed = [row for row in rows if row["event"] == "completed"]
        executions = {}
        for row in completed:
            executions[row["id"]] = executions.get(row["id"], 0) + 1
        jobs = {identifier: sum(row["event"] == "job" and set(record["job_args"]).issubset(row["args"])
                                for row in rows)
                for identifier, record in self.accepted.items() if record["kind"] == "job"}
        unknowns = []
        deadline = time.monotonic() + self.options.recovery_timeout
        for identifier, record in self.unknown.items():
            outcome = self.audit_unknown(identifier, record, rows, completed, deadline)
            unknowns.append({"id": identifier, "kind": record["kind"], "outcome": outcome})
        report = {"accepted": len(self.accepted), "unknown": unknowns,
                  "completed_deliveries": len(completed), "distinct_delivered_ids": len(executions),
                  "repeated_deliveries": {key: count for key, count in executions.items() if count > 1},
                  "job_executions": jobs,
                  "repeated_job_executions": {key: count for key, count in jobs.items() if count > 1}}
        (self.output / "accounting.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        return report

    def audit_unknown(self, identifier, record, rows, completed, deadline):
        if record["kind"] in ("set", "hashset", "file", "minority-probe"):
            return self.audit_stored_value(identifier, record, deadline)
        if record["kind"] == "job" and any(row["event"] == "job" and
                set(record["job_args"]).issubset(row["args"]) for row in rows):
            return "job-executed"
        return "executed" if any(row["id"] == identifier for row in completed) else "unresolved"

    def audit_stored_value(self, identifier, record, deadline):
        get = record["path"]
        if record["kind"] == "hashset":
            get = f"/data/hashsets/{identifier}"
        elif record["kind"] == "file":
            get = f"/data/files/{identifier}"
        values = []
        def read_all():
            nonlocal values
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError("Unknown-operation audit exceeded its phase deadline")
            values = [request(port, get, timeout=min(5, remaining)) for port in self.ports]
            if any(status not in (200, 404) for status, _, _ in values):
                raise AssertionError(f"Unknown write cannot be audited after recovery: {identifier}, {[r[0] for r in values]}")
            return True
        wait_until(f"audit unknown operation {identifier}", read_all, max(.001, deadline - time.monotonic()))
        if all(status == 200 and sha256(value) == record["sha256"] for status, _, value in values):
            return "committed-on-all-nodes"
        if all(status == 404 for status, _, _ in values):
            return "absent-at-final-observation"
        raise AssertionError(f"Unknown write has inconsistent final state: {identifier}")

    def absence(self, role, seconds):
        self.phase = f"absence-{role}-{seconds:g}s"
        self.ready()
        leader = self.leader()
        if leader is None:
            raise AssertionError("No leader before fault injection")
        nodes = [leader]
        if role != "leader":
            count = 1 if role == "follower" else 2
            nodes = [node for node in range(3) if node != leader][:count]
        pids = [self.node_pid(n) for n in nodes]
        stopping = threading.Event()
        started = time.monotonic()
        election_seconds = None
        minority = None
        def minority_write():
            result = self.mutate("minority-probe", f"/data/sets/minority-{uuid.uuid4().hex}", b"probe",
                                 port=self.ports[leader])
            return result, time.perf_counter()
        with ThreadPoolExecutor(max_workers=2) as pool:
            traffic = pool.submit(self.traffic, stopping)
            try:
                for pid in pids:
                    self.pause(pid, True)
                    self.suspended.add(pid)
                self.client.append({"event": "suspended", "phase": self.phase, "nodes": nodes, "pids": pids})
                # Direct minority write: a ready-gateway refusal alone does not
                # demonstrate the consensus invariant.
                minority = pool.submit(minority_write) if role == "quorum" else None
                election_seconds = self.monitor_absence(role, nodes, started, seconds, traffic, minority, stopping)
            finally:
                restoration_at = time.perf_counter()
                resumed = time.monotonic()
                try:
                    for pid in pids:
                        self.pause(pid, False)
                        self.suspended.remove(pid)
                    self.client.append({"event": "resumed", "phase": self.phase, "actual_absence_seconds": resumed - started})
                finally:
                    # A stale process identity must fail without leaving the
                    # producer alive while the executor waits for its threads.
                    stopping.set()
                    traffic.result()
        if minority:
            result, completed_at = minority.result()
            assert_minority_result(result, completed_at, restoration_at)
        self.ready()
        ready_seconds = time.monotonic() - resumed
        self.verify()
        self.reconcile()
        self.audit()
        self.capture(self.phase)
        result = {"phase": self.phase, "fault": "process-suspension", "nodes": nodes,
                  "requested_absence_seconds": seconds, "actual_absence_seconds": resumed - started,
                  "new_leader_observed_seconds": election_seconds, "ready_after_resume_seconds": ready_seconds,
                  "full_recovery_seconds": time.monotonic() - resumed,
                  "accepted": len(self.accepted), "unknown": len(self.unknown), "passed": True}
        self.results.append(result)
        print(json.dumps(result), flush=True)

    def monitor_absence(self, role, nodes, started, seconds, traffic, minority, stopping):
        deadline = started + seconds
        heartbeat = started
        election_seconds = None
        while time.monotonic() < deadline:
            if traffic.done():
                traffic.result()
            if role == "leader" and election_seconds is None and self.leader(excluded=nodes) is not None:
                election_seconds = time.monotonic() - started
            if minority and minority.done():
                result, completed_at = minority.result()
                assert_minority_result(result, completed_at, time.perf_counter())
            if time.monotonic() - heartbeat >= 30:
                heartbeat = time.monotonic()
                self.heartbeat(nodes, heartbeat - started)
            stopping.wait(min(.2, max(0, deadline - time.monotonic())))
        return election_seconds

    def heartbeat(self, nodes, elapsed):
        print(json.dumps({"phase": self.phase, "elapsed_seconds": elapsed,
                          "accepted": len(self.accepted), "unknown": len(self.unknown)}), flush=True)
        # Sample one reachable node; slow diagnostics do not count as traffic.
        observed = next(node for node in range(3) if node not in nodes)
        try:
            status, _, metrics = request(self.ports[observed], "/metrics", timeout=.2)
            if status == 200:
                self.record_metrics(self.phase, observed, metrics)
        except NETWORK_ERRORS:
            pass

    def chaos(self):
        self.seed()
        for seconds in self.options.durations:
            for role in self.options.roles.split(","):
                self.absence(role, seconds)

    def seed(self):
        if self.seeded:
            return
        self.phase = "confirmed-before-fault"
        for kind, collection in (("set", "sets"), ("hashset", "hashsets"), ("file", "files")):
            identifier = uuid.uuid4().hex
            value = payload(2097152 if kind == "file" else 65536, identifier)
            path = f"/data/{collection}/{identifier}" if kind == "set" else f"/data/{collection}?id={identifier}"
            self.require_mutation(kind, path, value, identifier=identifier)
            self.expected[f"/data/{collection}/{identifier}"] = sha256(value)
        self.require_mutation("async", "/async-function/chaos/consume", payload(2097152), expected_status=202)
        response, _ = self.require_mutation("stream", "/publish-stream/chaos-stream/consume", payload(65536), expected_status=202)
        self.expected[f"/data/streams/chaos-stream/messages/{json.loads(response)['MessageId']}/body"] = sha256(payload(65536))
        self.require_mutation("counter", "/data/sets/chaos-mixed-counter/incr", b"")
        identifier = uuid.uuid4().hex
        self.require_mutation("job", "/job/chaos-job", json.dumps({"Args": [identifier], "TtlSecondsAfterFinished": 3600}).encode(),
                              identifier=identifier, headers={"Content-Type": "application/json"}, expected_status=202)
        self.reconcile()
        self.verify()
        self.seeded = True

    def websocket(self):
        asyncio.run(WebSocketCampaign(self).run())

    def websocket_timeout(self):
        asyncio.run(WebSocketCampaign(self, profile="timeout").owner_timeout())

    def job_environments(self):
        self.phase = "job-environments"
        identifier = uuid.uuid4().hex
        environments = [{"Name": "CHAOS_JOB_EXPECTED_ID", "Value": identifier},
            {"Name": "UNRESOLVED_SECRET", "Value": "", "SecretRef": {"Name": "secret", "Key": "key"}},
            {"Name": "UNRESOLVED_CONFIG", "Value": "", "ConfigMapRef": {"Name": "config", "Key": "key"}},
            {"Name": "UNRESOLVED_FIELD", "Value": "", "FieldRef": {"FieldPath": "metadata.name"}},
            {"Name": "UNRESOLVED_RESOURCE", "Value": "", "ResourceFieldRef": {
                "ContainerName": "main", "Resource": "limits.cpu", "Divisor": "1"}}]
        self.require_mutation("job", "/job/chaos-job", json.dumps({
            "Args": [identifier], "Environments": environments}).encode(), identifier=identifier,
            headers={"Content-Type": "application/json"}, expected_status=202)
        self.reconcile()
        self.results.append({"phase": self.phase, "id": identifier, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def start_gated_job(self):
        identifier = uuid.uuid4().hex
        gate = self.output / f"job-gate-{identifier}"
        body, _ = self.require_mutation("job", "/job/chaos-job", json.dumps({
            "Args": [identifier, "gate=" + str(gate)]
        }).encode(), identifier=identifier, headers={"Content-Type": "application/json"}, expected_status=202)
        queue_id = json.loads(body)["Id"]
        wait_until("job process entered the independent gate", lambda:
            any(row.get("event") == "job-started" and identifier in row["args"]
                for row in read_receipts(self.receipts)), self.options.recovery_timeout)

        def acknowledged():
            for port in self.ports:
                status, _, response = request(port, "/job/chaos-job")
                if status != 200:
                    return False
                jobs = [row for row in json.loads(response) if row["Id"] == queue_id]
                if len(jobs) != 1 or jobs[0]["Status"] != "Running":
                    return False
            return True

        wait_until("running job no longer appears in the Raft queue", acknowledged, self.options.recovery_timeout)
        return identifier, gate, queue_id

    def job_recovery(self):
        self.phase = "job-supervisor-recovery"
        identifier, gate, queue_id = self.start_gated_job()
        self.client.append({"event": "job-acknowledged-before-supervisor-stop", "id": identifier,
                            "queue_id": queue_id})
        self.stop()
        self.start()
        gate.write_bytes(b"release")
        self.reconcile()
        self.results.append({"phase": self.phase, "id": identifier, "queue_id": queue_id, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def completed_job_checkpoint(self, queue_id):
        checkpoint = {}
        def check():
            copies = []
            for port in self.ports:
                status, _, body = request(port, "/job/chaos-job")
                if status != 200:
                    return False
                rows = [row for row in json.loads(body) if row["Id"] == queue_id]
                if len(rows) != 1 or rows[0]["Status"] != "Succeeded":
                    return False
                copies.append({key: rows[0][key] for key in ("Name", "Id", "Status", "StartTimestamp")})
            if any(row != copies[0] for row in copies):
                raise AssertionError(f"Job checkpoint differs across nodes: {copies!r}")
            checkpoint.update(copies[0])
            return True
        wait_until("completed job checkpoint on every node", check, self.options.recovery_timeout)
        return checkpoint

    def lease_burst(self):
        """Keep a majority writable when a silent follower follows a write burst."""
        self.phase = "leader-lease-burst"
        leader = self.leader()
        if leader is None:
            raise AssertionError("No leader before the lease burst")
        follower = next(node for node in range(3) if node != leader)
        pid = self.node_pid(follower)
        path = f"/data/sets/lease-burst-{uuid.uuid4().hex}"

        def burst():
            until = time.monotonic() + 3
            count = 0
            while time.monotonic() < until:
                value = payload(256, uuid.uuid4().hex)
                self.require_mutation("set", path, value, port=self.ports[leader])
                self.expected[path] = sha256(value)
                count += 1
                time.sleep(.1)
            return count

        healthy_writes = burst()
        self.pause(pid, True)
        self.suspended.add(pid)
        self.client.append({"event": "lease-follower-suspended", "node": follower, "pid": pid})
        try:
            silent_writes = burst()
            # An idle heartbeat following a burst exercises a different lease
            # path from continuous traffic. Do not retry the following write.
            time.sleep(1)
            value = payload(256, uuid.uuid4().hex)
            started = time.monotonic()
            self.require_mutation("set", path, value, port=self.ports[leader], timeout=15)
            probe_seconds = time.monotonic() - started
            self.expected[path] = sha256(value)
        finally:
            self.pause(pid, False)
            self.suspended.remove(pid)
            self.client.append({"event": "lease-follower-resumed", "node": follower, "pid": pid})
        self.ready()
        self.verify()
        self.results.append({"phase": self.phase, "leader": leader, "silent_follower": follower,
                             "healthy_writes": healthy_writes, "silent_writes": silent_writes,
                             "probe_seconds": probe_seconds, "probe_budget_seconds": 15, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def leader_route(self):
        self.phase = "stale-leader-http-route"
        leader = self.leader()
        if leader is None:
            raise AssertionError("No leader before route test")
        ingress = (leader + 1) % 3
        path = "/data/sets/leader-route-" + uuid.uuid4().hex
        self.require_mutation("set", path, b"warmup", port=self.ports[ingress])
        pid = self.node_pid(leader)
        started = time.monotonic()
        with ThreadPoolExecutor(max_workers=1) as pool:
            self.pause(pid, True)
            self.suspended.add(pid)
            self.client.append({"event": "route-leader-suspended", "node": leader, "pid": pid})
            try:
                pending = pool.submit(self.require_mutation, "set", path, b"after-election",
                                      port=self.ports[ingress])
                wait_until("new leader outside paused route", lambda:
                    self.leader(excluded=(leader,)) is not None, self.options.recovery_timeout)
                pending.result()
            finally:
                self.pause(pid, False)
                self.suspended.remove(pid)
        self.expected[path] = sha256(b"after-election")
        self.ready()
        self.verify()
        self.results.append({"phase": self.phase, "ingress_node": ingress,
                             "old_leader": leader, "seconds": time.monotonic() - started, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def startup_jobs(self):
        self.phase = "startup-job-configuration"
        identifier = uuid.uuid4().hex
        self.require_mutation("job", "/job/chaos-job",
            json.dumps({"Args": [identifier], "TtlSecondsAfterFinished": 3600}).encode(),
            identifier=identifier, headers={"Content-Type": "application/json"}, expected_status=202)
        self.reconcile()
        status, _, _, _ = self.mutate("invalid-job", "/job/missing-configuration",
            json.dumps({"Args": ["must-not-execute"]}).encode(), headers={"Content-Type": "application/json"})
        if status != 400:
            raise AssertionError(f"Undefined native job was not rejected before enqueue: HTTP {status}")
        self.results.append({"phase": self.phase, "job_argument": identifier, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def network_latency(self):
        if sys.platform != "linux" or os.geteuid() != 0 or os.readlink("/proc/self/ns/net") == os.readlink("/proc/1/ns/net"):
            raise RuntimeError("Network faults require root inside a separate network namespace; use .bin/run-local-network-chaos.sh")
        self.seed()
        self.phase = "network-latency"
        stopping = threading.Event()
        with ThreadPoolExecutor(max_workers=1) as pool:
            subprocess.run(["tc", "qdisc", "add", "dev", "lo", "root", "netem", "delay", "25ms"], check=True)
            started = time.monotonic()
            traffic = pool.submit(self.traffic, stopping)
            try:
                self.client.append({"event": "network-latency", "delay_ms": 25})
                while time.monotonic() - started < max(self.options.durations):
                    if traffic.done():
                        traffic.result()
                    stopping.wait(.2)
            finally:
                try:
                    subprocess.run(["tc", "qdisc", "del", "dev", "lo", "root"], check=True)
                    restored = time.monotonic()
                    self.client.append({"event": "network-latency-restored"})
                finally:
                    stopping.set()
                    traffic.result()
        self.ready()
        self.verify()
        self.reconcile()
        self.audit()
        self.capture(self.phase)
        self.results.append({"phase": self.phase, "delay_ms": 25,
                             "actual_fault_seconds": restored - started,
                             "full_recovery_seconds": time.monotonic() - restored, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def partitions(self):
        """Isolate every Raft listener while HTTP/functions continue running."""
        if sys.platform != "linux" or os.geteuid() != 0 or os.readlink("/proc/self/ns/net") == os.readlink("/proc/1/ns/net"):
            raise RuntimeError("Network faults require root inside a separate network namespace; use .bin/run-local-network-chaos.sh")
        self.seed()
        rule = ["OUTPUT", "-p", "tcp", "--dport", f"{self.raft}:{self.raft + 2}",
                "-m", "comment", "--comment", "slimdata-local-chaos", "-j", "DROP"]
        for seconds in self.options.durations:
            self.phase = f"network-partition-{seconds:g}s"
            self.ready()
            leader = self.leader()
            if leader is None:
                raise AssertionError("No leader before network partition")
            stopping = threading.Event()
            with ThreadPoolExecutor(max_workers=2) as pool:
                traffic = pool.submit(self.traffic, stopping)
                def minority_write(leader=leader):
                    result = self.mutate("minority-probe", f"/data/sets/partition-{uuid.uuid4().hex}",
                                         b"partition", port=self.ports[leader])
                    return result, time.perf_counter()
                installed = False
                try:
                    subprocess.run(["iptables", "-I"] + rule, check=True)
                    installed = True
                    started = time.monotonic()
                    self.client.append({"event": "network-partition", "phase": self.phase, "raft_ports": [self.raft, self.raft + 2]})
                    probe = pool.submit(minority_write)
                    deadline = started + seconds
                    while time.monotonic() < deadline:
                        if traffic.done():
                            traffic.result()
                        if probe.done():
                            result, completed_at = probe.result()
                            assert_minority_result(result, completed_at, time.perf_counter())
                        stopping.wait(min(.2, max(0, deadline - time.monotonic())))
                finally:
                    restoration_at = time.perf_counter()
                    try:
                        if installed:
                            subprocess.run(["iptables", "-D"] + rule, check=True)
                        resumed = time.monotonic()
                        self.client.append({"event": "network-restored", "phase": self.phase})
                    finally:
                        stopping.set()
                        traffic.result()
            result, completed_at = probe.result()
            assert_minority_result(result, completed_at, restoration_at)
            self.ready()
            self.verify()
            self.reconcile()
            self.audit()
            self.capture(self.phase)
            result = {"phase": self.phase, "fault": "all-Raft-links-dropped",
                      "actual_absence_seconds": resumed - started,
                      "full_recovery_seconds": time.monotonic() - resumed, "passed": True}
            self.results.append(result)
            print(json.dumps(result), flush=True)

    def disk_full(self, pause_raft=False):
        """Exhaust only a marked, small, separate test filesystem, then recover."""
        volume = self.state_root.parent
        marker = volume / ".slimdata-chaos-volume"
        if sys.platform != "linux" or not marker.is_file() or marker.read_text().strip() != "slimdata-local-chaos-v1":
            raise RuntimeError("Disk faults require the isolated volume from .bin/run-local-disk-chaos.sh")
        space = os.statvfs(volume)
        if os.stat(volume).st_dev == os.stat(self.output).st_dev or space.f_blocks * space.f_frsize > 256 * 1024 * 1024:
            raise RuntimeError("Refusing to exhaust a shared or larger-than-256-MiB filesystem")
        self.seed()
        filler = volume / "chaos-filler"
        self.phase = "job-journal-disk-full" if pause_raft else "disk-full"
        job_id, job_gate, queue_id = self.start_gated_job()
        paused = []
        stopping = threading.Event()
        with ThreadPoolExecutor(max_workers=1) as pool:
            traffic = pool.submit(self.traffic, stopping)
            try:
                if pause_raft:
                    for node in range(3):
                        pid = self.node_pid(node)
                        self.pause(pid, True)
                        paused.append(pid)
                        self.suspended.add(pid)
                    self.client.append({"event": "journal-fault-raft-paused", "pids": paused})
                with filler.open("xb", buffering=0) as stream:
                    for block_size in (65536, 4096, 1):
                        block = bytes(block_size)
                        try:
                            while True:
                                stream.write(block)
                        except OSError as error:
                            if error.errno != errno.ENOSPC:
                                raise
                self.client.append({"event": "disk-full", "free_bytes": os.statvfs(volume).f_bfree * space.f_frsize})
                started = time.monotonic()
                job_gate.write_bytes(b"release")
                self.client.append({"event": "job-released-while-disk-full", "id": job_id, "queue_id": queue_id})
                wait_until("gated job completes while its journal volume is full", lambda:
                    any(row.get("event") == "job" and job_id in row["args"]
                        for row in read_receipts(self.receipts)), min(15, self.options.recovery_timeout))
                while time.monotonic() - started < self.options.durations[0]:
                    if traffic.done():
                        traffic.result()
                    stopping.wait(.2)
            finally:
                try:
                    # This removes only the harness-created filler, never WAL,
                    # snapshots, uploaded artifacts or runtime state.
                    filler.unlink(missing_ok=True)
                    resumed = time.monotonic()
                    self.client.append({"event": "disk-space-restored"})
                    for pid in paused:
                        try:
                            self.pause(pid, False)
                        except ProcessLookupError:
                            self.client.append({"event": "paused-node-exited-during-journal-fault", "pid": pid})
                        self.suspended.discard(pid)
                finally:
                    stopping.set()
                    traffic.result()
        self.ready()
        self.verify()
        self.reconcile()
        self.audit()
        if pause_raft:
            before_restart = self.completed_job_checkpoint(queue_id)
            self.stop()
            self.start()
            after_restart = self.completed_job_checkpoint(queue_id)
            if before_restart != after_restart:
                raise AssertionError(f"Confirmed terminal job replayed after restart: {before_restart!r} -> {after_restart!r}")
            self.verify()
            self.reconcile()
            self.audit()
            self.client.append({"event": "job-journal-checkpoint-retained", "id": job_id,
                                "checkpoint": after_restart})
        self.capture(self.phase)
        self.results.append({"phase": self.phase, "full_recovery_seconds": time.monotonic() - resumed, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)

    def restarts(self):
        self.phase = "crash-restarts"
        for role in ("follower", "leader", "follower"):
            leader = self.leader()
            node = leader if role == "leader" else (leader + 1) % 3
            pid = self.node_pid(node)
            stopping = threading.Event()
            with ThreadPoolExecutor(max_workers=1) as pool:
                running = pool.submit(self.traffic, stopping)
                try:
                    self.kill_node(pid)
                    self.client.append({"event": "killed", "node": node, "pid": pid})
                    wait_until("supervisor restarted owned node", lambda node=node, pid=pid: self.node_pid(node) != pid,
                               self.options.recovery_timeout)
                    self.ready()
                finally:
                    stopping.set()
                    running.result()
            self.verify()
            self.reconcile()
        self.phase = "full-persistent-restart"
        self.stop()
        self.start()
        self.verify()
        self.reconcile()
        self.results.append({"phase": self.phase, "passed": True})
        print(json.dumps(self.results[-1]), flush=True)


class WebSocketCampaign:
    """Optional official SDK fixture; every mutation still uses the main ledger."""

    def __init__(self, campaign, profile="default"):
        from slimfaas_client import SlimFaasClient, SlimFaasClientConfig, SubscribeStreamConfig
        self.campaign = campaign
        self.client_type = SlimFaasClient
        config_path = campaign.output / ("websocket.json" if profile == "default" else "websocket-timeout.json")
        prior = next((record["path"].split("/")[2] for record in campaign.accepted.values()
                      if record["kind"] == "async" and record["path"].startswith("/async-function/chaos-ws-") and
                      (record["phase"] == "websocket-owner-timeout") == (profile == "timeout")), None)
        self.function = prior or "chaos-ws-" + uuid.uuid4().hex
        if config_path.is_file():
            self.function = json.loads(config_path.read_text())["function"]
        self.stream = "stream-" + self.function
        config_path.write_text(json.dumps({"function": self.function, "stream": self.stream}), encoding="utf-8")
        self.config = SlimFaasClientConfig(function_name=self.function, subscribe_streams=[SubscribeStreamConfig(self.stream)])
        self.journal = Journal(campaign.receipts / f"receipts-websocket-{os.getpid()}.jsonl")
        self.parallel = asyncio.Semaphore(campaign.options.concurrency)

    async def consume(self, req):
        headers = {key.lower(): value[0] for key, value in req.headers.items() if value}
        value = req.body or b""
        await asyncio.to_thread(self.journal.append, {"event": "completed", "id": headers.get("x-chaos-id", ""),
                             "bytes": len(value), "sha256": sha256(value),
                             "expected_sha256": headers.get("x-chaos-sha256", ""),
                             "status": 200, "attempt": req.try_number,
                             "element_id": req.element_id, "transport": "websocket"})
        return 200

    @staticmethod
    async def sync(req):
        value = await req.body.readall()
        await req.response.start(201, {"Content-Length": [str(len(value))]})
        for start in range(0, len(value), 65536):
            await req.response.write(value[start:start + 65536])
        await req.response.complete()

    async def wait_registered(self, client, running):
        while not client.is_connected:
            if running.done():
                raise AssertionError("Official WebSocket client stopped before registration")
            await asyncio.sleep(.05)

    async def connect(self, node):
        campaign = self.campaign
        client = self.client_type(WEBSOCKET_ENDPOINTS.get(campaign.ports[node], f"ws://127.0.0.1:{campaign.ports[node]}/ws"), self.config, reconnect_delay=.2)
        client.on_async_request(self.consume)
        client.on_stream_message(self.consume)
        client.on_sync_request(self.sync)
        running = asyncio.create_task(client.run_forever())
        try:
            await asyncio.wait_for(self.wait_registered(client, running), campaign.options.recovery_timeout)
            await asyncio.to_thread(wait_until, "durable WebSocket subscription", lambda:
                request(campaign.entry, f"/data/streams/{self.stream}/functions/{self.function}")[0] == 200,
                campaign.options.recovery_timeout)
            await asyncio.to_thread(wait_until, "WebSocket configuration on every node", lambda:
                all(self.function.encode() in request(port, "/status-functions")[2] for port in campaign.ports),
                campaign.options.recovery_timeout)
            return client, running
        except BaseException:
            await self.disconnect(client, running)
            raise

    @staticmethod
    async def disconnect(client, running):
        await client.close()
        await asyncio.wait_for(running, 15)

    async def publish(self, port):
        identifier = uuid.uuid4().hex
        value = payload(2097152, identifier)
        body, _ = await asyncio.to_thread(self.campaign.require_mutation, "stream",
            f"/publish-stream/{self.stream}/consume", value, identifier=identifier,
            port=port, expected_status=202)
        receipt = json.loads(body)
        self.campaign.expected[f"/data/streams/{self.stream}/messages/{receipt['MessageId']}/body"] = sha256(value)
        return identifier

    async def sync_check(self, port):
        value = payload(2097152)
        body, _ = await asyncio.to_thread(self.campaign.require_mutation, "sync-websocket",
            f"/function/{self.function}/echo", value, port=port, expected_status=201)
        if body != value:
            raise AssertionError("HTTP-over-WebSocket streaming body mismatch")

    async def cross_node(self):
        campaign = self.campaign
        campaign.phase = "websocket-cross-node"
        await asyncio.gather(*(self.sync_check(port) for port in campaign.ports))
        cases = [(size, chunked) for size in campaign.options.payload_sizes for chunked in (False, True)]
        await asyncio.gather(*(self.async_check(size, chunked, campaign.ports[index % 3])
                               for index, (size, chunked) in enumerate(cases)))
        identifiers = await asyncio.gather(*(self.publish(campaign.ports[index % 3]) for index in range(4)))
        await self.observe(identifiers)

    async def async_check(self, size, chunked, port):
        async with self.parallel:
            identifier = uuid.uuid4().hex
            _, _ = await asyncio.to_thread(self.campaign.require_mutation, "async",
                f"/async-function/{self.function}/consume", payload(size, identifier),
                identifier=identifier, port=port, expected_status=202, chunked=chunked)
            await asyncio.to_thread(self.campaign.observe, identifier)
            self.campaign.results.append({"phase": "websocket-async", "bytes": size,
                                          "chunked": chunked, "port": port, "passed": True})
            print(json.dumps(self.campaign.results[-1]), flush=True)

    async def observe(self, identifiers):
        for identifier in identifiers:
            await asyncio.to_thread(self.campaign.observe, identifier)

    async def owner_timeout(self):
        campaign = self.campaign
        campaign.phase = "websocket-owner-timeout"
        # Override only the delivery timeout. Retry statuses remain the actual
        # server defaults; this is the regression under test.
        self.config.configuration = json.dumps({"DefaultAsync": {"HttpTimeout": 8}})
        client, running = await self.connect(0)
        pid = campaign.node_pid(0)
        identifier = uuid.uuid4().hex
        paused = False
        try:
            campaign.pause(pid, True)
            paused = True
            campaign.suspended.add(pid)
            campaign.client.append({"event": "owner-suspended", "node": 0, "pid": pid})
            await asyncio.to_thread(wait_until, "leader available outside suspended owner",
                lambda: campaign.leader(excluded=(0,)) is not None, campaign.options.recovery_timeout)
            await asyncio.to_thread(campaign.require_mutation, "async", f"/async-function/{self.function}/consume",
                payload(2097152, identifier), identifier=identifier, port=campaign.ports[1], expected_status=202)
            started = time.monotonic()
            await asyncio.sleep(20)
            campaign.pause(pid, False)
            paused = False
            campaign.suspended.remove(pid)
            campaign.client.append({"event": "owner-resumed", "node": 0, "pid": pid,
                                    "seconds_after_acceptance": time.monotonic() - started})
            await asyncio.to_thread(campaign.ready)
            await asyncio.to_thread(campaign.observe, identifier)
            campaign.results.append({"phase": campaign.phase, "passed": True,
                "delivery_timeout_seconds": 8, "owner_absence_after_acceptance_seconds": 20,
                "id": identifier, "default_retry_statuses": True})
            print(json.dumps(campaign.results[-1]), flush=True)
        finally:
            if paused:
                campaign.pause(pid, False)
                campaign.suspended.discard(pid)
            await self.disconnect(client, running)

    async def run(self):
        campaign = self.campaign
        client, running = await self.connect(0)
        try:
            await asyncio.to_thread(campaign.reconcile)
            await self.cross_node()
            await self.disconnect(client, running)
            campaign.phase = "websocket-offline-retention"
            identifiers = [await self.publish(campaign.entry) for _ in range(3)]
            client, running = await self.connect(2)
            await self.observe(identifiers)
            await asyncio.to_thread(campaign.restarts)
            campaign.phase = "websocket-recovery"
            await self.observe([await self.publish(campaign.entry)])
            await asyncio.to_thread(wait_until, "WebSocket contiguous ACK progress", lambda:
                json.loads(request(campaign.entry, f"/data/streams/{self.stream}/functions/{self.function}")[2])["Lag"] == 0,
                campaign.options.recovery_timeout)
            await asyncio.to_thread(campaign.verify)
            await asyncio.to_thread(campaign.reconcile)
            campaign.results.append({"phase": "websocket", "function": self.function, "stream": self.stream, "passed": True})
            print(json.dumps(campaign.results[-1]), flush=True)
        finally:
            await self.disconnect(client, running)


def parse_arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, default=ROOT / "src/SlimFaas/bin/Release/net11.0/SlimFaas.dll")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--backend-config", type=Path, help="Owned Kubernetes campaign configuration; omit for Local")
    parser.add_argument("--source-commit", help="Exact source SHA when running an exported fixture outside a git checkout")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--state-directory", type=Path, help="Separate persistent storage, required for isolated disk-full tests")
    parser.add_argument("--resume", action="store_true", help="Resume a retained run after rechecking its confirmed ledger; keep the original port base")
    parser.add_argument("--port-base", type=int, default=35120)
    parser.add_argument("--node-log-level", choices=["Warning", "Information", "Debug"], default="Warning")
    parser.add_argument("--delivery-diagnostics", action="store_true", help="Debug delivery results without enabling verbose Raft/HTTP logs")
    parser.add_argument("--payload-sizes", type=lambda value: [int(x) for x in value.split(",")], default=SIZES)
    parser.add_argument("--concurrency", type=int, default=4)
    parser.add_argument("--request-timeout", type=float, default=15)
    parser.add_argument("--recovery-timeout", type=float, default=180)
    parser.add_argument("--scenarios", default="payloads,api,files,wake,retries,chaos,restarts")
    parser.add_argument("--durations", type=lambda value: [float(x) for x in value.split(",")], default=[1, 5, 10])
    parser.add_argument("--roles", default="follower,leader,quorum")
    parser.add_argument("--traffic-interval", type=float, default=1)
    options = parser.parse_args(argv)
    if not options.runtime.is_file():
        parser.error("--runtime must be a built SlimFaas DLL or executable")
    if options.source_commit is not None and re.fullmatch(r"[0-9a-fA-F]{40}", options.source_commit) is None:
        parser.error("--source-commit must be a full 40-character commit SHA")
    if options.concurrency < 1 or options.request_timeout <= 0 or options.recovery_timeout <= 0 or options.traffic_interval < .05:
        parser.error("concurrency/timeouts must be positive; traffic interval must be >= 0.05 seconds")
    if not options.payload_sizes or any(size < 0 or size > 20 * 1024 * 1024 for size in options.payload_sizes):
        parser.error("fixture payload sizes must be between 0 and 20 MiB")
    if any(seconds <= 0 for seconds in options.durations):
        parser.error("absence durations must be positive")
    if set(options.roles.split(",")) - {"follower", "leader", "quorum"}:
        parser.error("roles must be follower, leader or quorum")
    if set(options.scenarios.split(",")) - {"payloads", "api", "files", "wake", "retries", "streams", "schedules", "websocket", "websocket-timeout", "lease-burst", "leader-route", "protocol-timeout", "job-environments", "job-recovery", "startup-jobs", "job-during-node-stop", "chaos", "latency", "partitions", "diskfull", "job-diskfull", "restarts"}:
        parser.error("unknown scenario")
    if not 1024 <= options.port_base <= 65475:
        parser.error("port base must reserve 61 nonprivileged ports")
    return options


def main():
    options = parse_arguments()
    if options.backend_config is None:
        campaign = Campaign(options)
    else:
        from slimdata_kubernetes_backend import create_campaign
        campaign = create_campaign(options, globals())
    metadata = {"started_utc": time.time(), "platform": platform.platform(), "python": sys.version,
                "environment_overrides": {name: os.environ[name] for name in (
                    "SlimData__WalMemoryManagement", "SlimData__SnapshotIntervalEntries",
                    "SlimData__SnapshotIntervalBytes") if name in os.environ},
                "commit": options.source_commit or subprocess.check_output(["git", "-c", f"safe.directory={ROOT}", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "runtime_sha256": sha256(campaign.runtime.read_bytes()),
                "slimdata_sha256": sha256((campaign.runtime.parent / "SlimData.dll").read_bytes())
                    if (campaign.runtime.parent / "SlimData.dll").is_file() else None,
                "harness_sha256": sha256(Path(__file__).read_bytes()),
                "arguments": {key: str(value) if isinstance(value, Path) else value for key, value in vars(options).items()}}
    parameter_name = f"parameters-resume-{campaign.generation}.json" if options.resume else "parameters.json"
    (campaign.output / parameter_name).write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    passed = False
    error = None
    try:
        campaign.start()
        if options.resume:
            resume_check_started = time.monotonic()
            campaign.verify()
            if "websocket" not in options.scenarios.split(","):
                campaign.reconcile()
            campaign.audit()
            campaign.client.append({"event": "resume-verified", "phase": campaign.phase,
                                    "seconds": time.monotonic() - resume_check_started})
        campaign.capture("initial")
        for scenario in options.scenarios.split(","):
            {"payloads": campaign.payloads, "api": campaign.api_smoke,
             "files": campaign.file_boundaries, "retries": campaign.retries_dlq_jobs,
             "wake": campaign.wake_and_cancel,
             "schedules": campaign.schedules,
             "streams": campaign.stream_contracts,
             "websocket": campaign.websocket,
             "websocket-timeout": campaign.websocket_timeout,
             "lease-burst": campaign.lease_burst,
             "leader-route": campaign.leader_route,
             "job-recovery": campaign.job_recovery,
             "job-environments": campaign.job_environments,
             "startup-jobs": campaign.startup_jobs,
             "job-during-node-stop": campaign.job_during_node_stop,
             "protocol-timeout": campaign.protocol_timeout,
             "chaos": campaign.chaos, "latency": campaign.network_latency, "partitions": campaign.partitions,
             "diskfull": campaign.disk_full,
             "job-diskfull": lambda: campaign.disk_full(pause_raft=True), "restarts": campaign.restarts}[scenario]()
        campaign.reconcile()
        campaign.audit()
        campaign.capture("final")
        passed = True
    except BaseException as failure:
        error = repr(failure)
        try:
            campaign.capture("failure")
        except Exception as capture_error:
            error += "; failure metrics: " + repr(capture_error)
        raise
    finally:
        try:
            campaign.stop()
        except BaseException as failure:
            passed = False
            error = repr(failure) if error is None else error + "; cleanup: " + repr(failure)
            raise
        finally:
            report = {"passed": passed, "error": error, "results": campaign.results,
                      "accepted": len(campaign.accepted), "unknown": len(campaign.unknown),
                      "missing": getattr(campaign, "missing", []), "finished_utc": time.time()}
            (campaign.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
