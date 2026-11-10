#!/usr/bin/env python3
"""Exercise the source demo on three nodes, including a persistent restart."""
import argparse
import json
import os
import signal
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from contextlib import contextmanager
from pathlib import Path

BASE_URL = "http://127.0.0.1:30020"


def request(route, method="GET", data=None):
    headers = {}
    if isinstance(data, dict):
        data = json.dumps(data).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE_URL + route, data=data, method=method, headers=headers)
    with urllib.request.urlopen(req, timeout=120) as response:
        return response.status, response.read()


def node_is_ready(port):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/ready", timeout=2) as response:
            return response.status == 200 and response.read() == b"READY"
    except urllib.error.HTTPError as error:
        error.close()
    except (urllib.error.URLError, ConnectionError, TimeoutError):
        pass
    return False


def wait_for_ready(process):
    """Poll readiness only; mutations are never retried."""
    deadline = time.monotonic() + 180
    for port in (30020, 30021, 30022, 30023):
        while True:
            if process.poll() is not None:
                raise RuntimeError("Local supervisor exited during startup")
            if node_is_ready(port):
                break
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Node at port {port} never became ready")
            time.sleep(.2)


def wait_for_job(job_id):
    deadline = time.monotonic() + 120
    while True:
        jobs = json.loads(request("/job/fibonacci")[1])
        matching = [item for item in jobs if item["Id"] == job_id]
        if matching and matching[0]["Status"] == "Succeeded":
            return
        if matching and matching[0]["Status"] == "Failed":
            raise RuntimeError(f"Smoke job failed: {matching}")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"Smoke job did not succeed: {jobs}")
        time.sleep(.2)


def exercise_cluster(first_start):
    assert len(json.loads(request("/status-functions")[1])) == 4
    assert b"<html" in request("/")[1].lower(), "Dashboard missing"
    assert request("/function/fibonacci1/hello/local")[1] == b"Hello local!"
    if first_start:
        result = json.loads(request("/function/fibonacci1/fibonacci", "POST", {"Input": 10})[1])
        assert result["result"] == 55, result
        assert request("/data/sets/dotnet11-restart", "POST", b"persisted")[0] == 200
        job = json.loads(request("/job/fibonacci", "POST",
                                 {"Args": ["10"], "TtlSecondsAfterFinished": 60})[1])
        wait_for_job(job["Id"])
    assert request("/data/sets/dotnet11-restart")[1] == b"persisted"


def stop_process(process):
    if process.poll() is not None:
        return
    process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
    try:
        process.wait(timeout=45)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=True)
        else:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)


@contextmanager
def running_cluster(command, root, log_path):
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=root, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=os.name != "nt",
                                   creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
        try:
            wait_for_ready(process)
            yield
        except BaseException:
            log.flush()
            print(log_path.read_text(encoding="utf-8", errors="replace")[-14000:])
            raise
        finally:
            stop_process(process)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("artifacts/native-local"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    runtime = args.runtime.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    command = (["dotnet", str(runtime)] if runtime.suffix == ".dll" else [str(runtime)]) + ["local"]
    with tempfile.TemporaryDirectory(prefix="slimfaas-native-local-") as temporary:
        directory = Path(temporary)
        overlay = directory / "state.yaml"
        overlay.write_text("state:\n  mode: persistent\n  directory: " +
                           json.dumps(str(directory / "state")) + "\n", encoding="utf-8")
        manifests = ["-f", str(root / "slimfaas.local.yaml"), "-f", str(overlay)]
        subprocess.run(command + ["validate"] + manifests, cwd=root, check=True, timeout=60)
        for attempt in range(2):
            with running_cluster(command + ["up"] + manifests, root, output / f"startup-{attempt}.log"):
                exercise_cluster(first_start=attempt == 0)
    print("Native local passed: three nodes, dashboard, sync, job, persistent restart.")


if __name__ == "__main__":
    main()
