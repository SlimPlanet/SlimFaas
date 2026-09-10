#!/usr/bin/env python3
"""Smoke-test a running slimfaas.local.external-metrics.yaml demo."""

import argparse
import json
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--slimfaas", default="http://127.0.0.1:30020")
    parser.add_argument("--exporter", default="http://127.0.0.1:9090")
    parser.add_argument("--timeout", type=float, default=90)
    parser.add_argument("--node-http-port-base", type=int, default=30021)
    args = parser.parse_args()

    def request(url, method="GET", payload=None):
        headers = {"Content-Type": "application/json"} if payload is not None else {}
        with urlopen(Request(url, data=payload, method=method, headers=headers), timeout=5) as response:
            return response.read().decode()

    def control(path):
        # Do not retry mutations: a failed control operation must fail the smoke test.
        request(args.exporter + path, "PUT")

    def status():
        return next(f for f in json.loads(request(args.slimfaas + "/status-functions")) if f["Name"] == "worker")

    def wait_for(description, condition):
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            try:
                if condition():
                    print(description, flush=True)
                    return
            except (URLError, TimeoutError, StopIteration):
                pass
            time.sleep(0.25)
        raise TimeoutError(description + " did not complete")

    def replicas(count):
        current = status()
        return current["NumberRequested"] == count and current["NumberReady"] == count

    def leader_port():
        for port in range(args.node_http_port_base, args.node_http_port_base + 3):
            metrics = request(f"http://127.0.0.1:{port}/metrics")
            if 'slimfaas_scaler_source_available{function="worker",source="jobs",provider="prometheus"} 1' in metrics:
                return port
        raise RuntimeError("No leader has successfully scraped the exporter")

    control("/available?value=true")
    control("/pending?value=0")
    wait_for("Initial zero replicas verified", lambda: replicas(0))
    control("/pending?value=73")
    wait_for("External 0 -> 8 wake verified without calling the worker", lambda: replicas(8))
    port = leader_port()
    value = json.loads(request(f"http://127.0.0.1:{port}/debug/promql/eval", "POST",
        json.dumps({"deployment": "worker", "source": "jobs", "query": 'sum(jobs_pending{queue="emails"})'}).encode()))
    assert value.get("value", value.get("Value")) == 73, value
    assert request(args.slimfaas + "/function/worker/hello/local").strip() == "Hello local!"
    print("Source-scoped debug query and function routing verified", flush=True)

    control("/available?value=false")

    def invalid():
        try:
            request(f"http://127.0.0.1:{port}/debug/promql/eval", "POST",
                b'{"deployment":"worker","source":"jobs","query":"jobs_pending"}')
            return False
        except HTTPError as error:
            return error.code == 400

    wait_for("Exporter failure observed by provider", invalid)
    control("/pending?value=0")
    # Verify continuously beyond the demo's stabilization window, while telemetry is unavailable.
    until = time.monotonic() + 22
    while time.monotonic() < until:
        assert replicas(8), status()
        time.sleep(0.25)
    print("Capacity held throughout exporter failure", flush=True)
    control("/available?value=true")
    wait_for("Recovered empty queue: 8 -> 0 verified after stabilization", lambda: replicas(0))


if __name__ == "__main__":
    main()
