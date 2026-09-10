#!/usr/bin/env python3
"""Read-only scaling playground/SSE smoke against the sleeping external-metrics demo."""
import argparse
import json
import subprocess
import threading
import time
from pathlib import Path
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nodes", default="http://127.0.0.1:30021,http://127.0.0.1:30022,http://127.0.0.1:30023")
    parser.add_argument("--function", default="worker")
    parser.add_argument("--viewers", type=int, default=6)
    parser.add_argument("--seconds", type=float, default=15)
    parser.add_argument("--pids", default="", help="Optional comma-separated native process IDs for RSS sampling")
    parser.add_argument("--report", default="artifacts/scaling-dashboard-smoke.json")
    args = parser.parse_args()
    nodes = args.nodes.split(",")
    stop = threading.Event()
    frames, errors, samples = [], [], []
    lock = threading.Lock()
    ready = [threading.Event() for _ in range(args.viewers)]

    def request(base, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        headers = {} if body is None else {"Content-Type": "application/json"}
        with urlopen(Request(base + path, data=data, headers=headers), timeout=10) as response:
            return json.load(response)

    def assert_sleeping():
        status = next(f for f in request(nodes[0], "/status-functions") if f["Name"] == args.function)
        assert status["NumberRequested"] == 0 and status["NumberReady"] == 0, status

    def watch(index):
        try:
            with urlopen(nodes[index % len(nodes)] + "/status-scaling-stream?function=" + args.function, timeout=10) as response:
                while not stop.is_set():
                    line = response.readline().decode()
                    if not line:
                        raise RuntimeError("Unexpected SSE disconnection")
                    if not line.startswith("data: "):
                        continue
                    state = json.loads(line[6:])
                    if state.get("Error"):
                        raise RuntimeError(state["Error"])
                    if state.get("Status") != "Live":
                        continue
                    assert state["Function"] == args.function
                    with lock:
                        frames.append({"viewer": index, "session": state["Session"], "time": state["ServerTimeMs"]})
                    ready[index].set()
        except Exception as error:
            if not stop.is_set():
                with lock:
                    errors.append(str(error))

    def memory():
        if not args.pids:
            return
        output = subprocess.check_output(["ps", "-o", "pid=,rss=", "-p", args.pids], text=True)
        samples.append({"time": time.time(), "rssMiB": {parts[0]: int(parts[1]) / 1024
            for parts in (line.split() for line in output.splitlines())}})

    assert_sleeping()
    memory()
    threads = [threading.Thread(target=watch, args=(i,), daemon=True) for i in range(args.viewers)]
    for thread in threads:
        thread.start()
    try:
        for index, event in enumerate(ready):
            assert event.wait(20), f"Viewer {index} did not receive a live frame: {errors}"
        for node in nodes:
            preview = request(node, "/debug/scaling/simulate", {"Function": args.function, "CurrentReplicas": 0,
                "ScaleFromZero": True, "Triggers": [{"Index": 0, "Query": "sum(jobs_pending)", "Source": "jobs",
                    "MetricType": "AverageValue", "Threshold": 10, "Value": 73}]})
            assert preview["Current"]["Target"] == 0, preview
            assert preview["Simulated"]["RawTarget"] == 8, preview
            assert preview["Simulated"]["Target"] == 8, preview
            assert preview["Simulated"]["Application"] == "Preview", preview
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            assert_sleeping()
            memory()
            stop.wait(1)
    finally:
        stop.set()
        for thread in threads:
            thread.join(11)
    assert not any(thread.is_alive() for thread in threads), "Viewer did not close"
    assert not errors, errors
    assert_sleeping()
    report = {"viewers": args.viewers, "frames": len(frames), "nodes": len(nodes),
        "sessions": sorted({frame["session"] for frame in frames}), "errors": errors,
        "workerStayedAsleep": True, "memory": samples}
    path = Path(args.report)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2))
    print(json.dumps({key: value for key, value in report.items() if key != "memory"}))


if __name__ == "__main__":
    main()
