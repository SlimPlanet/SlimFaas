#!/usr/bin/env python3
"""Exercise the container probe against real HTTP and unavailable endpoints."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import socket
import subprocess
import threading
import unittest

PROBE = Path(__file__).resolve().parents[1] / 'samples/Fibonacci/healthcheck.sh'


class HealthProbeTests(unittest.TestCase):
    def probe(self, port):
        return subprocess.run(['bash', str(PROBE), str(port)], capture_output=True, timeout=5, check=False)

    def serve_and_probe(self, status):
        paths = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                paths.append(self.path)
                self.send_response(status)
                self.end_headers()

            def log_message(self, *args):
                pass

        with ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                result = self.probe(server.server_port)
            finally:
                server.shutdown()
                thread.join()
        self.assertEqual(paths, ['/health'])
        return result

    def test_healthy_http_endpoint_succeeds(self):
        result = self.serve_and_probe(200)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_unhealthy_http_endpoint_fails(self):
        result = self.serve_and_probe(503)
        self.assertNotEqual(result.returncode, 0)

    def test_redirect_does_not_count_as_healthy(self):
        self.assertNotEqual(self.serve_and_probe(302).returncode, 0)

    def test_unavailable_endpoint_fails(self):
        with socket.socket() as unavailable:
            unavailable.bind(('127.0.0.1', 0))
            port = unavailable.getsockname()[1]
        self.assertNotEqual(self.probe(port).returncode, 0)

    def test_unresponsive_endpoint_fails_within_timeout(self):
        with socket.socket() as stalled:
            stalled.bind(('127.0.0.1', 0))
            stalled.listen()
            self.assertNotEqual(self.probe(stalled.getsockname()[1]).returncode, 0)


if __name__ == '__main__':
    unittest.main()
