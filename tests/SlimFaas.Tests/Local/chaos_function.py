#!/usr/bin/env python3
"""Real HTTP function/job fixture for the three-node Local reliability campaign.

Each process owns a separate fsynced receipt journal. Request bodies are hashed
incrementally so the fixture does not hide truncated/offloaded payloads.
"""

import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import threading
import time
import uuid
from urllib.request import build_opener, ProxyHandler, Request
from urllib.parse import parse_qs, urlsplit


class Journal:
    def __init__(self, directory):
        directory.mkdir(parents=True, exist_ok=True)
        self.path = directory / f"receipts-{os.getpid()}-{uuid.uuid4().hex}.jsonl"
        self.lock = threading.Lock()
        self.remote = os.environ.get("CHAOS_RECEIPT_URL")
        self.http = build_opener(ProxyHandler({}))

    def append(self, value):
        encoded = (json.dumps(value | {"utc": time.time(), "pid": os.getpid(),
                                      "function": os.environ.get("CHAOS_FUNCTION", "")}) + "\n").encode()
        with self.lock, self.path.open("ab", buffering=0) as stream:
            stream.write(encoded)
            os.fsync(stream.fileno())
        if self.remote:
            request = Request(self.remote, data=encoded, headers={"Content-Type": "application/json"}, method="POST")
            with self.http.open(request, timeout=10) as response:
                if response.status != 204:
                    raise RuntimeError("Independent receipt collector did not acknowledge the fsynced journal")


def hash_body(stream, length=None, chunked=False):
    digest = hashlib.sha256()
    size = 0
    if chunked:
        while True:
            line = stream.readline(8192)
            count = int(line.split(b";", 1)[0].strip(), 16)
            if count == 0:
                while True:
                    trailer = stream.readline(8192)
                    if trailer in (b"\r\n", b"\n"):
                        break
                    if not trailer:
                        raise EOFError("Truncated trailers")
                break
            remaining = count
            while remaining:
                block = stream.read(min(65536, remaining))
                if not block:
                    raise EOFError("Truncated chunk")
                digest.update(block)
                size += len(block)
                remaining -= len(block)
            if stream.read(2) != b"\r\n":
                raise ValueError("Invalid chunk terminator")
    else:
        remaining = length or 0
        while remaining:
            block = stream.read(min(65536, remaining))
            if not block:
                raise EOFError("Truncated request body")
            digest.update(block)
            size += len(block)
            remaining -= len(block)
    return size, digest.hexdigest()


def handler(journal, repair):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_args):
            pass

        def respond(self, status, value):
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            self.respond(200, {"healthy": True})

        def do_POST(self):
            size, digest = hash_body(self.rfile,
                                     int(self.headers.get("Content-Length", "0")),
                                     self.headers.get("Transfer-Encoding", "").lower() == "chunked")
            query = parse_qs(urlsplit(self.path).query)
            attempt = int(self.headers.get("SlimFaas-Try-Number", "0"))
            request_id = self.headers.get("X-Chaos-Id", "")
            expected = self.headers.get("X-Chaos-Sha256", digest)
            receipt = {"id": request_id, "bytes": size, "sha256": digest,
                       "expected_sha256": expected, "attempt": attempt,
                       "last_try": self.headers.get("Slimfaas-Last-Try", ""),
                       "element_id": self.headers.get("SlimFaas-Element-Id", ""),
                       "path": self.path, "event": "started"}
            journal.append(receipt)
            delay = float(query.get("delay", ["0"])[0])
            if delay:
                time.sleep(min(delay, 60))
            fail = query.get("fail", ["0"])[0]
            failed = (fail == "always" and not repair.exists()) or (fail.isdigit() and attempt < int(fail))
            status = 422 if digest != expected else 500 if failed else int(query.get("status", ["200"])[0])
            receipt |= {"event": "completed", "status": status}
            journal.append(receipt)
            try:
                self.respond(status, receipt)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                # Completion is durable in the independent receipt journal even
                # when the caller/worker disappears before receiving its reply.
                pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--journal-directory", type=Path, required=True)
    parser.add_argument("--repair", type=Path)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--job", action="store_true")
    parser.add_argument("args", nargs="*")
    options = parser.parse_args()
    journal = Journal(options.journal_directory)
    if options.job:
        expected_id = os.environ.get("CHAOS_JOB_EXPECTED_ID")
        if expected_id is not None and options.args != [expected_id]:
            raise ValueError("Job environment or arguments were not preserved")
        gate = os.environ.get("CHAOS_JOB_GATE") or next(
            (arg.removeprefix("gate=") for arg in options.args if arg.startswith("gate=")), None)
        if gate:
            journal.append({"event": "job-started", "args": options.args})
            deadline = time.monotonic() + 180
            while not Path(gate).exists():
                if time.monotonic() >= deadline:
                    raise TimeoutError("The job recovery gate was not released")
                time.sleep(.05)
        journal.append({"event": "job", "args": options.args})
        return
    repair = options.repair or options.journal_directory / "repair"
    server = ThreadingHTTPServer((options.host, int(os.environ["PORT"])), handler(journal, repair))
    server.serve_forever()


if __name__ == "__main__":
    main()
