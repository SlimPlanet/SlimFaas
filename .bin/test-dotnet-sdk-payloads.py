#!/usr/bin/env python3
"""Verify the real FibonacciBatch .NET worker's cross-node sync/async payloads.

Start the demo and an independent fibonacci-ws Job first; see docs/benchmarking.md.
Async hashes come from the worker's actual decoded body in the supervisor log,
in addition to its independently persisted processing proof. Mutations are not retried.
"""

import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import time
from urllib.parse import urlsplit
import uuid


SIZES = (65_536, 262_144, 1_048_576, 2_000_000, 2_097_152, 4_194_304)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def request(origin, path, method="GET", body=None, chunked=False):
    address = urlsplit(origin)
    if address.scheme not in ("http", "https") or address.path not in ("", "/") or address.username or address.query or address.fragment:
        raise ValueError("Node URLs must be HTTP(S) origins")
    factory = http.client.HTTPSConnection if address.scheme == "https" else http.client.HTTPConnection
    connection = factory(address.hostname, address.port, timeout=120)
    try:
        headers = {"Content-Type": "application/json"} if body is not None else {}
        outgoing = body
        if chunked:
            outgoing = (body[index:index + 65_536] for index in range(0, len(body), 65_536))
        elif body is not None:
            headers["Content-Length"] = str(len(body))
        connection.request(method, path, body=outgoing, headers=headers, encode_chunked=chunked)
        response = connection.getresponse()
        return response.status, dict(response.getheaders()), response.read()
    finally:
        connection.close()


class WorkerLog:
    def __init__(self, path):
        self.path = path
        self.position = path.stat().st_size
        self.observations = {}

    def read(self):
        with self.path.open("rb") as stream:
            stream.seek(self.position)
            while True:
                line = stream.readline()
                # The parent may still be copying a multi-megabyte stdout line.
                # Keep the cursor before that line until it is complete.
                if not line or not line.endswith(b"\n"):
                    break
                self.position = stream.tell()
                marker = b"[WS]   Corps     : "
                if marker in line:
                    body = line.split(marker, 1)[1].rstrip(b"\r\n")
                    value = json.loads(body)
                    if isinstance(value, dict) and isinstance(value.get("nonce"), str):
                        self.observations.setdefault(value["nonce"], []).append(digest(body))

    def observe(self, nonce, timeout=30):
        deadline = time.monotonic() + timeout
        while True:
            self.read()
            if nonce in self.observations:
                return self.observations[nonce]
            if time.monotonic() >= deadline:
                raise AssertionError("The worker log has no complete decoded-body observation for " + nonce)
            time.sleep(.05)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--function", required=True)
    parser.add_argument("--worker-log", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--base-url", default="http://127.0.0.1:30020")
    parser.add_argument("--node-urls", default="http://127.0.0.1:30021,http://127.0.0.1:30022,http://127.0.0.1:30023")
    parser.add_argument("--sdk-file", type=Path)
    options = parser.parse_args()
    if not re.fullmatch(r"[a-zA-Z0-9_.-]+", options.function):
        parser.error("Provide the unique virtual-function name of the test Job")
    options.output.mkdir(parents=True, exist_ok=False)
    observer = WorkerLog(options.worker_log)
    rows = []
    report = {"passed": False, "function": options.function, "source_sha256": digest(Path(__file__).read_bytes()),
              "sdk_sha256": digest(options.sdk_file.read_bytes()) if options.sdk_file else None, "rows": rows}

    def journal(record):
        with (options.output / "client.jsonl").open("ab", buffering=0) as stream:
            stream.write((json.dumps(record | {"utc": time.time()}) + "\n").encode())
            os.fsync(stream.fileno())

    try:
        deadline = time.monotonic() + 120
        nodes = options.node_urls.split(",")
        while True:
            ready = True
            for node in nodes:
                code, _, status = request(node, "/status-functions")
                if code != 200 or not any(row["Name"] == options.function and row["NumberReady"] > 0 for row in json.loads(status)):
                    ready = False
            if ready:
                break
            if time.monotonic() >= deadline:
                raise AssertionError("The .NET virtual function did not become ready on every node")
            time.sleep(.1)
        for size in SIZES:
            for chunked in (False, True):
                for node in nodes:
                    for mode in ("sync", "async"):
                        nonce = uuid.uuid4().hex
                        value = {"input": 10, "nonce": nonce, "padding": ""}
                        length = len(json.dumps(value, separators=(",", ":")).encode())
                        value["padding"] = "x" * (size - length)
                        body = json.dumps(value, separators=(",", ":")).encode()
                        assert len(body) == size
                        record = {"mode": mode, "node": node, "chunked": chunked, "bytes": size,
                                  "nonce": nonce, "sha256": digest(body)}
                        journal(record | {"event": "intent"})
                        path = "/function/" + options.function + "/probe" if mode == "sync" else (
                            "/async-function/" + options.function + "/tour/ws/" + options.function + "/async")
                        try:
                            status, headers, response = request(node, path, "POST", body, chunked)
                        except OSError as error:
                            journal(record | {"event": "unknown", "error": repr(error)})
                            raise
                        expected_status = 200 if mode == "sync" else 202
                        journal(record | {"event": "accepted" if status == expected_status else "unknown" if status >= 500 else "rejected",
                                          "status": status, "headers": headers})
                        if status != expected_status:
                            raise AssertionError(f"{mode} returned HTTP {status}")
                        if mode == "sync":
                            actual = digest(json.dumps(json.loads(response)["request"], separators=(",", ":")).encode())
                        else:
                            deadline = time.monotonic() + 180
                            while True:
                                code, _, proof = request(options.base_url, "/data/sets/" + options.function + "-async")
                                if code == 200:
                                    proof = json.loads(proof)
                                    if proof.get("nonce") == nonce:
                                        assert proof["function"] == options.function and proof["result"] == 55
                                        break
                                elif code != 404:
                                    raise AssertionError(f"Processing proof read failed: HTTP {code}")
                                if time.monotonic() >= deadline:
                                    raise AssertionError("No independent .NET processing proof for " + nonce)
                                time.sleep(.1)
                            observed = observer.observe(nonce)
                            if len(observed) != 1:
                                raise AssertionError("Duplicate .NET body observations: " + nonce)
                            actual = observed[0]
                        if actual != record["sha256"]:
                            raise AssertionError(".NET SDK payload checksum mismatch: " + nonce)
                        rows.append(record | {"status": status, "actual_sha256": actual, "passed": True})
                        print(json.dumps(rows[-1]), flush=True)
        report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
        raise
    finally:
        report["finished_utc"] = time.time()
        (options.output / "report.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
