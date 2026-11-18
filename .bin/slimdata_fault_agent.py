"""Signal only the owned SlimFaas runtime in this test pod's PID namespace."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import signal
import time


def runtime_identity(include_binaries=True):
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
            for name in (("SlimFaas", "SlimFaas.dll", "SlimData.dll", "DotNext.Net.Cluster.dll") if include_binaries else ()):
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


def read_runtime_statistics(identity, proc_root=Path("/proc")):
    """Read process-owned live sockets separately from the whole pod's TIME_WAIT."""
    process = proc_root / str(identity["pid"])
    fields = (process / "stat").read_text().rsplit(")", 1)[1].split()
    if fields[19] != identity["started"]:
        raise RuntimeError("Runtime process changed before statistics collection")
    descriptors = list((process / "fd").iterdir())
    socket_inodes = []
    for descriptor in descriptors:
        try:
            target = str(descriptor.readlink())
            if target.startswith("socket:[") and target.endswith("]"):
                socket_inodes.append(target[8:-1])
        except FileNotFoundError:
            pass # A request can close an FD while this read-only sample is taken.
    owned = set(socket_inodes)
    process_states, namespace_states = Counter(), Counter()
    for family in ("tcp", "tcp6"):
        for line in (process / "net" / family).read_text().splitlines()[1:]:
            row = line.split()
            namespace_states[row[3]] += 1
            if row[9] in owned:
                process_states[row[3]] += 1
    interfaces = {}
    for line in (process / "net/dev").read_text().splitlines()[2:]:
        name, values = line.split(":", 1)
        counters = values.split()
        interfaces[name.strip()] = {"rx_bytes": int(counters[0]), "tx_bytes": int(counters[8])}
    if (process / "stat").read_text().rsplit(")", 1)[1].split()[19] != identity["started"]:
        raise RuntimeError("Runtime process changed during statistics collection")
    return {"utc": time.time(), "monotonic": time.monotonic(), **identity,
        "user_cpu_ticks": int(fields[11]), "system_cpu_ticks": int(fields[12]),
        "clock_ticks_per_second": os.sysconf("SC_CLK_TCK"),
        "rss_bytes": int(fields[21]) * os.sysconf("SC_PAGE_SIZE"),
        "open_fds": len(descriptors), "socket_fds": len(socket_inodes),
        "process_tcp_states_hex": dict(process_states),
        "pod_network_namespace_tcp_states_hex": dict(namespace_states),
        "pod_network_namespace_interfaces": interfaces,
        "scope": "Process FDs and owned live TCP; TIME_WAIT and interface bytes cover the pod network namespace including diagnostic/probe traffic. Non-atomic read-only sample."}


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
            if self.path == "/stats":
                self.reply(200, read_runtime_statistics(runtime_identity(include_binaries=False)))
            elif self.path == "/status":
                self.reply(200, runtime_identity())
            else:
                raise ValueError("Unknown endpoint")
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
