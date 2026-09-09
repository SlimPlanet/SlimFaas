#!/usr/bin/env python3
"""Read-only native peer polling comparison. Run against an otherwise idle demo."""
import argparse
from collections import deque
import json
import threading
import time
import urllib.request


def metrics(url):
    with urllib.request.urlopen(url.rstrip('/') + '/metrics', timeout=5) as response:
        lines = response.read().decode().splitlines()
    def scalar(name):
        return next((float(line.split()[1]) for line in lines if line.startswith(name + ' ')), 0)
    peer_lines = [line for line in lines if line.startswith('http_requests_received_total{') and '/internal/activity-events' in line]
    requests = sum(float(line.rsplit(' ', 1)[1]) for line in peer_lines)
    failures = sum(float(line.rsplit(' ', 1)[1]) for line in peer_lines if 'code="2xx"' not in line)
    return {'peerRequests': requests, 'peerFailures': failures, 'cpuSeconds': scalar('process_cpu_seconds_total'),
            'rssBytes': scalar('process_working_set_bytes')}


class Stream:
    def __init__(self, url):
        self.url = url
        self.ready = threading.Event()
        self.stopped = threading.Event()
        self.events = deque(maxlen=5000)
        self.error = None
        self.thread = threading.Thread(target=self.read, daemon=True)
        self.thread.start()
        if not self.ready.wait(10):
            raise RuntimeError('SSE did not send its initial state')
        if self.error:
            raise self.error

    def read(self):
        try:
            with urllib.request.urlopen(self.url, timeout=10) as response:
                kind = ''
                for raw in response:
                    if self.stopped.is_set():
                        break
                    line = raw.decode().strip()
                    if line.startswith('event: '):
                        kind = line[7:]
                    if line.startswith('data: '):
                        self.receive(kind, json.loads(line[6:]))
        except Exception as error:
            self.error = error
            self.ready.set()

    def receive(self, kind, data):
        if kind == 'state':
            self.ready.set()
        elif kind in ('activity', 'activity_batch'):
            self.events.extend(data if isinstance(data, list) else [data])

    def close(self):
        self.stopped.set()
        self.thread.join(12)
        if self.thread.is_alive():
            raise RuntimeError('SSE connection did not close')
        if self.error:
            raise self.error


def measure(nodes, seconds, activity):
    stream = Stream(nodes[0].rstrip('/') + '/status-functions-stream' + ('' if activity else '?activity=false'))
    try:
        time.sleep(3)  # Exclude bootstrap and allow previous subscriptions to drain.
        before = [metrics(node) for node in nodes]
        start = time.monotonic()
        peak = sum(node['rssBytes'] for node in before)
        for _ in range(seconds):
            time.sleep(1)
            after = [metrics(node) for node in nodes]
            peak = max(peak, sum(node['rssBytes'] for node in after))
        elapsed = time.monotonic() - start
        failures = sum(b['peerFailures'] - a['peerFailures'] for a, b in zip(before, after))
        if failures:
            raise RuntimeError(f'{failures} peer reads failed; fix routing before comparing polling cost')
        return {'peerFailures': failures, 'view': 'Traffic' if activity else 'Overview', 'seconds': elapsed,
                'peerRequests': sum(b['peerRequests'] - a['peerRequests'] for a, b in zip(before, after)),
                'nodeCpuSeconds': sum(b['cpuSeconds'] - a['cpuSeconds'] for a, b in zip(before, after)),
                'peakNodeRssBytes': peak, 'receivedEvents': len(stream.events)}
    finally:
        stream.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--nodes', nargs='+', default=['http://127.0.0.1:30021', 'http://127.0.0.1:30022', 'http://127.0.0.1:30023'])
    parser.add_argument('--seconds', type=int, default=20)
    parser.add_argument('--interval-label', default='500')
    args = parser.parse_args(argv)
    if args.seconds < 1:
        parser.error('--seconds must be positive')
    for active in (True, False):
        print(json.dumps({'configuredIntervalMs': args.interval_label, **measure(args.nodes, args.seconds, active)}), flush=True)


if __name__ == '__main__':
    main()
