"""Linux loopback IPv4/TCP byte counters; never records packet bodies.

Counts only outgoing loopback copies to avoid double accounting. TCP payload
includes HTTP/Raft framing and retransmissions. These are observed loopback
bytes, not physical-link throughput. See packet(7) for socket/drop semantics.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import socket
import struct
import subprocess
import threading
import time


class Capture:
    def __init__(self, ports):
        self.ports = set(ports)
        self.counts = Counter()
        self.stopping = threading.Event()
        self.socket = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.htons(3))
        self.socket.setsockopt(socket.SOL_SOCKET, 33, 128 * 1024 * 1024)  # SO_RCVBUFFORCE; CAP_NET_ADMIN
        self.receive_buffer_bytes = self.socket.getsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF)
        self.socket.bind(("lo", 0))
        self.socket.settimeout(.1)
        self.samples = []
        self.error = None
        self.drain_until = float("inf")
        self.started = time.monotonic()
        self.utc = time.time()
        self.thread = threading.Thread(target=self.guarded_run)

    def guarded_run(self):
        try:
            self.run()
        except Exception as error:
            self.error = error

    def run(self):
        last = self.started
        while not self.stopping.is_set() or time.monotonic() < self.drain_until:
            try:
                frame, address = self.socket.recvfrom(262144)
            except socket.timeout:
                continue
            if address[2] != 4 or len(frame) < 54 or frame[12:14] != b"\x08\x00":
                continue
            packet = memoryview(frame)[14:]
            ihl = (packet[0] & 15) * 4
            if packet[0] >> 4 != 4 or ihl < 20 or packet[9] != 6:
                continue
            total, fragment = struct.unpack_from("!HH", packet, 2)[0], struct.unpack_from("!H", packet, 6)[0]
            if fragment & 0x3fff or len(packet) < total or total < ihl + 20:
                self.counts["unparsed_ipv4_packets"] += 1
                continue
            source, destination = struct.unpack_from("!HH", packet, ihl)
            if source not in self.ports and destination not in self.ports:
                continue
            tcp_header = (packet[ihl + 12] >> 4) * 4
            if tcp_header < 20 or total < ihl + tcp_header:
                raise ValueError("Invalid captured TCP header")
            payload = total - ihl - tcp_header
            self.counts["packets"] += 1
            self.counts["ipv4_bytes"] += total
            self.counts["tcp_payload_bytes"] += payload
            self.counts["tcp_payload_to_raft_bytes" if destination in self.ports
                        else "tcp_payload_from_raft_bytes"] += payload
            now = time.monotonic()
            if now - last >= 1:
                self.samples.append({"utc": time.time(), "seconds": now-self.started, **self.counts})
                last = now

    def start(self):
        self.thread.start()
        return self

    def finish(self):
        self.drain_until = time.monotonic() + 1
        self.stopping.set()
        self.thread.join(2)
        if self.thread.is_alive():
            raise RuntimeError("Capture did not stop")
        packets, drops = struct.unpack("II", self.socket.getsockopt(263, 6, 8))
        self.socket.close()
        if self.error:
            raise self.error
        seconds = time.monotonic()-self.started
        return {"started_utc":self.utc, "seconds":seconds, "ports":sorted(self.ports),
            "interface":"lo", "protocol":"IPv4 TCP", "packet_direction":"PACKET_OUTGOING",
            "counts":dict(self.counts), "kernel_packets":packets,"kernel_drops":drops,
            "receive_buffer_bytes":self.receive_buffer_bytes,
            "tcp_payload_bytes_per_second":self.counts['tcp_payload_bytes']/seconds,
            "samples":self.samples}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ports',required=True,type=lambda s:[int(x) for x in s.split(',')])
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('command',nargs=argparse.REMAINDER)
    options=parser.parse_args()
    command=options.command
    if command and command[0]=='--': command=command[1:]
    if not command: parser.error('a child command is required')
    with options.output.open('x',encoding='utf-8') as output:
        capture=Capture(options.ports).start()
        try:
            result=subprocess.run(command,check=False)
        finally:
            report=capture.finish()
            json.dump(report,output,indent=2)
            output.write('\n')
    raise SystemExit(result.returncode or int(report['kernel_drops']>0))


if __name__=='__main__': main()
