"""Independent fsynced receipt collector for real Kubernetes Jobs."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import threading
import uuid


def build_server(directory, port):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"receipts-sink-{uuid.uuid4().hex}.jsonl"
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            self.send_response(200 if self.path == "/health" else 404)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def do_POST(self):
            size = int(self.headers.get("Content-Length", "0"))
            if self.path != "/receipts" or not 0 < size <= 65536:
                self.send_error(400)
                return
            value = json.loads(self.rfile.read(size))
            if not isinstance(value, dict) or value.get("event") not in ("job", "job-started", "started", "completed"):
                self.send_error(400)
                return
            encoded = (json.dumps(value) + "\n").encode()
            with lock, path.open("ab", buffering=0) as output:
                output.write(encoded)
                os.fsync(output.fileno())
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()

    return ThreadingHTTPServer(("0.0.0.0", port), Handler)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8081)
    options = parser.parse_args()
    build_server(options.directory, options.port).serve_forever()
