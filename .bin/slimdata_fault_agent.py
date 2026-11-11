"""Signal only the owned SlimFaas runtime in this test pod's PID namespace."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import json
import os
from pathlib import Path
import signal


def runtime_identity():
    matches = []
    for path in Path("/proc").iterdir():
        if not path.name.isdigit():
            continue
        try:
            if str((path / "exe").readlink()) != "/app/SlimFaas":
                continue
            if path.stat().st_uid != 1000:
                raise RuntimeError("Runtime UID differs from the image's appuser")
            # Field 22, after the parenthesized process name (which can contain spaces).
            started = (path / "stat").read_text().rsplit(")", 1)[1].split()[19]
            binaries = {}
            for name in ("SlimFaas", "SlimFaas.dll", "SlimData.dll", "DotNext.Net.Cluster.dll"):
                binary = path / "root/app" / name
                if binary.is_file():
                    with binary.open("rb") as source:
                        binaries[name] = hashlib.file_digest(source, "sha256").hexdigest()
            matches.append({"pid": int(path.name), "started": started, "pod_uid": os.environ["POD_UID"],
                            "binaries_sha256": binaries})
        except (FileNotFoundError, ProcessLookupError):
            continue
    if len(matches) != 1:
        raise RuntimeError(f"Expected one owned runtime, found {len(matches)}")
    return matches[0]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def reply(self, status, value):
        data = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        try:
            if self.path != "/status":
                raise ValueError("Unknown endpoint")
            self.reply(200, runtime_identity())
        except (OSError, ValueError, RuntimeError) as error:
            self.reply(503, {"error": str(error)})

    def do_POST(self):
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if self.path != "/control" or not 0 < size <= 4096:
                raise ValueError("Invalid control request")
            request = json.loads(self.rfile.read(size))
            identity = runtime_identity()
            if identity != request["identity"]:
                raise ValueError("Stale or foreign runtime process identity")
            operation = {"pause": signal.SIGSTOP, "resume": signal.SIGCONT, "kill": signal.SIGKILL}[request["action"]]
            os.kill(identity["pid"], operation)
            self.reply(200, identity)
        except (OSError, ValueError, RuntimeError, KeyError) as error:
            self.reply(409, {"error": str(error)})


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 39777), Handler).serve_forever()
