#!/usr/bin/env python3
"""Exercise owned native nodes: snapshots, rolling upgrades, pauses and quorum recovery.

Requires two published SlimFaas executables and Python 3.10+ on Linux/macOS.
Only processes started here are signalled. State and logs are retained in --output.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


HTTP_BASE = 32021
RAFT_BASE = 3382
DEADLINE = 60


def request(port, path, data=None, timeout=DEADLINE):
    headers = {"Content-Type": "application/octet-stream"} if data is not None else {}
    with urlopen(Request(f"http://127.0.0.1:{port}{path}", data=data, headers=headers),
                 timeout=timeout) as response:
        return response.read()


def eventually(description, predicate, timeout=DEADLINE):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if predicate():
                return
        except (URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(0.2)
    raise AssertionError(f"Timed out after {timeout}s: {description}")


class Cluster:
    def __init__(self, output):
        self.output = output
        self.processes = {}
        self.logs = []
        self.expected = {}
        self.results = []

    def start(self, node, binary):
        env = os.environ | {
            "HOSTNAME": f"slimfaas-{node}",
            "SlimFaas__Orchestrator": "Local",
            "SlimFaas__Namespace": "raft-recovery-test",
            "SlimFaas__BaseSlimDataUrl": "http://{pod_ip}:{pod_port_0}",  # NOSONAR: synthetic local-test traffic uses loopback peers only.
            "SlimFaas__BaseFunctionUrl": "http://{pod_ip}:{pod_port}",  # NOSONAR: synthetic local-test traffic uses loopback peers only.
            "SlimFaas__BaseFunctionPodUrl": "http://{pod_ip}:{pod_port}",  # NOSONAR: synthetic local-test traffic uses loopback peers only.
            "SlimFaas__EnableFront": "false",
            "SlimFaas__WebSocketPort": "0",
            "SlimFaas__Local__NodeCount": "3",
            "SlimFaas__Local__NodeNamePrefix": "slimfaas-",
            "SlimFaas__Local__SlimDataPortBase": str(RAFT_BASE),
            "SlimFaas__Local__HttpPortBase": str(HTTP_BASE),
            "SlimData__Directory": str(self.output / "state"),
            "SlimData__AllowColdStart": "true",
            "SlimData__SnapshotIntervalEntries": "50",
            "Data__DefaultVisibility": "Public",
            "Logging__LogLevel__Default": "Warning",
            "Logging__LogLevel__SlimFaas.Workers.SlimDataDiagnosticsWorker": "Information",
        }
        log = (self.output / f"node-{node}-{len(self.logs)}.log").open("wb")
        self.logs.append(log)
        self.processes[node] = subprocess.Popen([str(binary)], cwd=binary.parent,
                                                env=env, stdout=log, stderr=subprocess.STDOUT)

    def stop(self, node):
        process = self.processes.pop(node)
        if process.poll() is None:
            process.send_signal(signal.SIGCONT)
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)

    def close(self):
        while self.processes:
            self.stop(next(iter(self.processes)))
        for log in self.logs:
            log.close()

    def ready(self):
        for node, process in self.processes.items():
            def node_ready(n=node, p=process):
                assert p.poll() is None, f"Node {n} exited; inspect its log"
                return request(HTTP_BASE + n, "/ready", timeout=2)
            eventually(f"node {node} ready", node_ready)

    def leader(self):
        text = request(RAFT_BASE, "/SlimData/leader", timeout=5).decode()
        match = re.search(r"http://127\.0\.0\.1:(\d+)", text)  # NOSONAR: validates a loopback-only test URL.
        assert match, text
        node = int(match[1]) - RAFT_BASE
        assert node in self.processes, text
        return node

    def write(self, node, key):
        value = f"value-for-{key}".encode()
        # Never retry a write: a response timeout must fail the experiment.
        request(HTTP_BASE + node, f"/data/sets/{key}", value)
        self.expected[key] = value

    def verify(self, phase):
        started = time.monotonic()
        for node in self.processes:
            for key, value in self.expected.items():
                actual = request(HTTP_BASE + node, f"/data/sets/{key}")
                assert actual == value, f"{phase}: node {node}, key {key} differs"
            (self.output / f"{phase}-node-{node}.prom").write_bytes(
                request(HTTP_BASE + node, "/metrics"))
        result = {"phase": phase, "keys_per_node": len(self.expected), "nodes": 3,
                  "verification_seconds": round(time.monotonic() - started, 3)}
        self.results.append(result)
        print(json.dumps(result), flush=True)

    def idle_quorum_loss(self):
        """An empty apply backlog must not hide a prolonged consensus outage."""
        leader = self.leader()
        followers = [node for node in self.processes if node != leader]
        log_path = max(self.output.glob(f"node-{leader}-*.log"), key=lambda p: p.stat().st_mtime)
        notification = "SlimData Raft consensus unavailable."
        notifications_before = log_path.read_text().count(notification)

        def metrics():
            lines = request(HTTP_BASE + leader, "/metrics", timeout=2).decode().splitlines()
            return {parts[0]: float(parts[1]) for line in lines
                    if not line.startswith("#") and len(parts := line.split()) == 2
                    and parts[0].startswith("slimdata_raft_")}

        try:
            for node in followers:
                self.processes[node].send_signal(signal.SIGSTOP)

            def unavailable():
                observation = metrics()
                return (observation.get("slimdata_raft_has_leader") == 0 and
                        observation.get("slimdata_raft_consensus_unavailable_duration_seconds", 0) >= 65)

            # Wait on the diagnostic state, including its 60-second reminder,
            # while intentionally holding the fault. No application writes occur.
            eventually("idle quorum loss and reminder", unavailable, timeout=90)
            assert request(HTTP_BASE + leader, "/health", timeout=2) == b"OK"
            try:
                request(HTTP_BASE + leader, "/ready", timeout=2)
            except HTTPError as error:
                assert error.code == 503, error.code
            else:
                raise AssertionError("A node without quorum reported ready")
            observation = metrics()
            assert observation["slimdata_raft_last_log_index"] == observation["slimdata_raft_applied_log_index"], observation
            assert observation["slimdata_raft_progress_stalled"] == 0, observation
            (self.output / "idle-quorum-loss.prom").write_bytes(request(HTTP_BASE + leader, "/metrics"))
            assert log_path.read_text().count(notification) - notifications_before >= 2, "Missing transition/reminder"
        finally:
            for node in followers:
                if self.processes[node].poll() is None:
                    self.processes[node].send_signal(signal.SIGCONT)
        self.ready()
        eventually("consensus outage duration resets", lambda: metrics().get(
            "slimdata_raft_consensus_unavailable_duration_seconds") == 0)
        self.write(leader, "after-idle-quorum-loss")
        self.verify("idle-quorum-recovered")

    def pause_followers(self, count):
        leader = self.leader()
        followers = [node for node in self.processes if node != leader][:count]
        try:
            for node in followers:
                self.processes[node].send_signal(signal.SIGSTOP)
            with ThreadPoolExecutor(max_workers=1) as executor:
                pending = executor.submit(self.write, leader, f"pause-{count}")
                try:
                    if count == 1:
                        pending.result(timeout=15)
                    else:
                        # Controlled fault duration, not a readiness assumption.
                        time.sleep(6)
                        assert not pending.done(), "A minority acknowledged a write"
                finally:
                    resumed = time.monotonic()
                    for node in followers:
                        self.processes[node].send_signal(signal.SIGCONT)
                pending.result(timeout=30)
                self.results.append({"phase": f"pause-{count}-recovery",
                                     "seconds": round(time.monotonic() - resumed, 3)})
        finally:
            for node in followers:
                if self.processes[node].poll() is None:
                    self.processes[node].send_signal(signal.SIGCONT)
        self.ready()
        self.verify(f"pause-{count}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    baseline, candidate = args.baseline.resolve(), args.candidate.resolve()
    for binary in (baseline, candidate):
        assert binary.is_file() and os.access(binary, os.X_OK), f"Missing executable: {binary}"
    for port in [HTTP_BASE + i for i in range(3)] + [RAFT_BASE + i for i in range(3)]:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", port))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cluster = Cluster(output)
    metadata = {"baseline": str(baseline), "candidate": str(candidate),
                "baseline_sha256": hashlib.sha256(baseline.read_bytes()).hexdigest(),
                "candidate_sha256": hashlib.sha256(candidate.read_bytes()).hexdigest()}
    try:
        for node in range(3):
            cluster.start(node, baseline)
        cluster.ready()
        # Sequential commits cross multiple snapshot windows, without batching them
        # into a single WAL entry. Each write is acknowledged before it is expected.
        for index in range(180):
            cluster.write(index % 3, f"existing-{index}")
        cluster.verify("baseline")
        snapshots = [str(p.relative_to(output)) for p in output.glob("state/**/db/*") if p.is_file()]
        assert len(snapshots) >= 3, "Expected baseline snapshots on all three nodes"
        metadata["baseline_snapshots"] = snapshots
        leader = cluster.leader()
        for node in [n for n in range(3) if n != leader] + [leader]:
            cluster.stop(node)
            cluster.start(node, candidate)
            cluster.ready()
            cluster.write(node, f"upgraded-{node}")
            cluster.verify(f"upgrade-{node}")
        cluster.pause_followers(1)
        cluster.pause_followers(2)
        cluster.idle_quorum_loss()
        while cluster.processes:
            cluster.stop(next(iter(cluster.processes)))
        for node in range(3):
            cluster.start(node, candidate)
        cluster.ready()
        cluster.write(0, "after-full-restart")
        cluster.verify("full-restart")
        metadata["passed"] = True
    finally:
        metadata["results"] = cluster.results
        (output / "results.json").write_text(json.dumps(metadata, indent=2) + "\n")
        cluster.close()


if __name__ == "__main__":
    main()
