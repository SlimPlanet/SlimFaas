#!/usr/bin/env python3
"""Controllable OpenMetrics exporter and HTTP worker for the autoscaling demo."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


class Handler(BaseHTTPRequestHandler):
    pending = 0
    available = True
    worker = False

    def reply(self, status, body, content_type="text/plain; charset=utf-8"):
        content = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/health":
            self.reply(200, "OK\n")
        elif path == "/metrics" and not self.worker:
            if not Handler.available:
                self.reply(503, "Exporter temporarily unavailable\n")
            else:
                self.reply(200, '# TYPE jobs_pending gauge\n'
                           f'jobs_pending{{queue="emails"}} {Handler.pending}\n')
        elif path.startswith("/hello/") and self.worker:
            self.reply(200, f"Hello {path.removeprefix('/hello/')}!\n")
        else:
            self.reply(404, "Not found\n")

    def do_PUT(self):
        if self.worker:
            self.reply(404, "Not found\n")
            return
        parsed = urlparse(self.path)
        value = parse_qs(parsed.query).get("value", [""])[0]
        if parsed.path == "/pending" and value.isdecimal() and len(value) <= 9:
            Handler.pending = int(value)
        elif parsed.path == "/available" and value in ("true", "false"):
            Handler.available = value == "true"
        else:
            self.reply(400, "Use /pending?value=73 or /available?value=false\n")
            return
        self.reply(200, "OK\n")

    def log_message(self, format, *args):
        # Metrics polling is intentionally quiet in the guided demo.
        if self.command != "GET" or not self.path.startswith(("/health", "/metrics")):
            super().log_message(format, *args)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9090)
    parser.add_argument("--worker", action="store_true")
    args = parser.parse_args()
    Handler.worker = args.worker
    with ThreadingHTTPServer((args.host, args.port), Handler) as server:
        server.serve_forever()
