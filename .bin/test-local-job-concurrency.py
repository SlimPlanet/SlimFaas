#!/usr/bin/env python3
"""Verify that retained local jobs release concurrency slots before TTL cleanup.

Build SlimFaas and FibonacciBatch first, then run this script from any directory.
Uses only the Python standard library, temporary state, and loopback ports.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]


def yaml_mapping(mapping, indent=0):
    """Emit this fixture's block mappings with JSON-quoted scalars and lists."""
    for key, value in mapping.items():
        prefix = " " * indent + json.dumps(key) + ":"
        if isinstance(value, dict):
            yield prefix
            yield from yaml_mapping(value, indent + 2)
        else:
            yield prefix + " " + json.dumps(value)


def available_ports(count):
    # Reserve all ports together so no two allocations in this test coincide.
    sockets = []
    try:
        for _ in range(count):
            listener = socket.socket()
            listener.bind(("127.0.0.1", 0))
            sockets.append(listener)
        return [listener.getsockname()[1] for listener in sockets]
    finally:
        for listener in sockets:
            listener.close()


def stop(process):
    if process.poll() is not None:
        return
    process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False)
        else:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path,
                        default=ROOT / "src/SlimFaas/bin/Debug/net10.0/SlimFaas.dll")
    parser.add_argument("--batch", type=Path,
                        default=ROOT / "src/FibonacciBatch/bin/Debug/net10.0/FibonacciBatch.dll")
    args = parser.parse_args()
    runtime, batch = args.runtime.resolve(), args.batch.resolve()
    for path in (runtime, batch):
        if not path.is_file():
            parser.error(f"Build the workload first; missing {path}")
    dotnet = os.environ.get("DOTNET_HOST_PATH", "dotnet")
    runtime_command = [dotnet, str(runtime)] if runtime.suffix == ".dll" else [str(runtime)]
    batch_command = [dotnet, str(batch)] if batch.suffix == ".dll" else [str(batch)]
    entrypoint, node, raft = available_ports(3)

    with tempfile.TemporaryDirectory(prefix="slimfaas-job-concurrency-") as temporary:
        root = Path(temporary)
        manifest = {
            "schemaVersion": 1,
            "name": "job-concurrency",
            "cluster": {"nodes": 1, "entrypointPort": entrypoint,
                        "nodeHttpPortBase": node, "raftPortBase": raft},
            "state": {"mode": "persistent", "directory": str(root / "state")},
            "jobs": {
                name: {
                    "command": batch_command,
                    "workingDirectory": str(batch.parent),
                    "annotations": {"SlimFaas/Job": "true", "SlimFaas/DefaultVisibility": "Public",
                                    "SlimFaas/NumberParallelJob": "4"},
                    "ttlSecondsAfterFinished": 3600,
                    "backoffLimit": 0,
                    "restartPolicy": "Never",
                } for name in ("ttl-success", "ttl-failure")
            },
        }
        manifest_path = root / "local.yaml"
        manifest_path.write_text("\n".join(yaml_mapping(manifest)) + "\n", encoding="utf-8")
        subprocess.run([*runtime_command, "local", "validate", "-f", str(manifest_path)], check=True)

        def request(path, payload=None):
            data = None if payload is None else json.dumps(payload).encode()
            req = urllib.request.Request(f"http://127.0.0.1:{entrypoint}{path}", data=data,
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=5) as response:
                body = response.read()
                return json.loads(body) if body else None

        log_path = root / "supervisor.log"
        with log_path.open("w", encoding="utf-8") as log:
            process = subprocess.Popen(
                [*runtime_command, "local", "up", "-f", str(manifest_path)],
                stdout=log, stderr=subprocess.STDOUT, cwd=ROOT,
                start_new_session=os.name != "nt",
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
            try:
                deadline = time.monotonic() + 120
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("Local supervisor exited before readiness")
                    try:
                        with urllib.request.urlopen(f"http://127.0.0.1:{entrypoint}/ready", timeout=5):
                            break
                    except (urllib.error.URLError, ConnectionError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Local supervisor never became ready")
                        time.sleep(.25)

                def wait_for_jobs(name, expected):
                    deadline = time.monotonic() + 60
                    while True:
                        jobs = request(f"/job/{name}")
                        actual = {job["Id"]: job["Status"] for job in jobs}
                        if all(actual.get(job_id) == status for job_id, status in expected.items()):
                            return
                        if process.poll() is not None or time.monotonic() >= deadline:
                            raise TimeoutError(f"{name}: expected {expected}, observed {actual}")
                        time.sleep(.25)

                for name, argument, status in (("ttl-success", "10", "Succeeded"),
                                               ("ttl-failure", "invalid-number", "Failed")):
                    expected = {}
                    for _ in range(4):
                        # Mutations are issued once; only observation/readiness is polled.
                        job = request(f"/job/{name}", {"Args": [argument]})
                        expected[job["Id"]] = status
                    wait_for_jobs(name, expected)
                    fifth = request(f"/job/{name}", {"Args": ["10"]})
                    expected[fifth["Id"]] = "Succeeded"
                    wait_for_jobs(name, expected)
                    print(f"PASS {name}: fifth job succeeded; four {status} jobs still retained (TTL 3600s).",
                          flush=True)
            except BaseException:
                log.flush()
                print(log_path.read_text(encoding="utf-8", errors="replace")[-14000:])
                raise
            finally:
                stop(process)


if __name__ == "__main__":
    main()
