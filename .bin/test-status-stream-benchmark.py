#!/usr/bin/env python3
"""Test the peer benchmark and optionally emit Sonar generic coverage using stdlib trace."""
import argparse
from collections import deque
from contextlib import redirect_stderr, redirect_stdout
import dis
import importlib.util
import io
import json
from pathlib import Path
import threading
import trace
import types
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).with_name('status-stream-benchmark.py').resolve()
spec = importlib.util.spec_from_file_location('peer_benchmark', SCRIPT)
benchmark = importlib.util.module_from_spec(spec)


class BenchmarkTests(unittest.TestCase):
    def test_metrics_count_only_peer_reads_and_detect_unsuccessful_reads(self):
        text = ('process_cpu_seconds_total 2.5\nprocess_working_set_bytes 1024\n'
                'http_requests_received_total{endpoint="/internal/activity-events",code="2xx"} 12\n'
                'http_requests_received_total{endpoint="/internal/activity-events",code="4xx"} 3\n'
                'http_requests_received_total{endpoint="/metrics",code="2xx"} 99\n')
        with patch.object(benchmark.urllib.request, 'urlopen', return_value=io.BytesIO(text.encode())) as request:
            result = benchmark.metrics('http://node/')
        request.assert_called_once_with('http://node/metrics', timeout=5)
        self.assertEqual(result, {'peerRequests': 15, 'peerFailures': 3, 'cpuSeconds': 2.5, 'rssBytes': 1024})
        with patch.object(benchmark.urllib.request, 'urlopen', return_value=io.BytesIO(b'')):
            self.assertEqual(set(benchmark.metrics('http://node').values()), {0})

    def test_stream_accepts_single_and_batched_events_but_ignores_snapshot_history(self):
        body = (': heartbeat\nevent: state\ndata: {"RecentActivity":[{"Id":"history"}]}\n\n'
                'event: activity\ndata: {"Id":"single"}\n\n'
                'event: activity_batch\ndata: [{"Id":"batch"}]\n\n'
                'event: heartbeat\ndata: {}\n\n')
        with patch.object(benchmark.urllib.request, 'urlopen', return_value=io.BytesIO(body.encode())):
            stream = benchmark.Stream('http://node/status-functions-stream')
            stream.thread.join(1)
            stream.close()
        self.assertEqual([event['Id'] for event in stream.events], ['single', 'batch'])
        stream.receive('activity_batch', [{'Id': index} for index in range(6000)])
        self.assertEqual(len(stream.events), 5000)
        self.assertEqual(stream.events[0]['Id'], 1000)

    def test_stream_stops_reading_after_close_is_requested(self):
        stream = benchmark.Stream.__new__(benchmark.Stream)
        stream.url = 'http://node'; stream.stopped = threading.Event(); stream.stopped.set()
        stream.ready = threading.Event(); stream.events = deque(); stream.error = None
        with patch.object(benchmark.urllib.request, 'urlopen', return_value=io.BytesIO(b'event: activity\ndata: {}\n')):
            stream.read()
        self.assertEqual(len(stream.events), 0)
        self.assertIsNone(stream.error)

    def test_stream_connection_errors_and_invalid_json_are_reported(self):
        with patch.object(benchmark.urllib.request, 'urlopen', side_effect=OSError('connection refused')):
            with self.assertRaisesRegex(OSError, 'connection refused'):
                benchmark.Stream('http://node')
        with patch.object(benchmark.urllib.request, 'urlopen', return_value=io.BytesIO(b'event: state\ndata: invalid\n')):
            with self.assertRaises(json.JSONDecodeError):
                benchmark.Stream('http://node')

    def test_stream_readiness_timeout_and_incomplete_cleanup_are_reported(self):
        with patch.object(benchmark.threading, 'Thread'), patch.object(benchmark.threading.Event, 'wait', return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'initial state'):
                benchmark.Stream('http://node')
        stream = benchmark.Stream.__new__(benchmark.Stream)
        stream.stopped = threading.Event(); stream.thread = Mock(); stream.error = None
        stream.thread.is_alive.return_value = True
        with self.assertRaisesRegex(RuntimeError, 'did not close'):
            stream.close()
        stream.thread.is_alive.return_value = False
        stream.error = OSError('late failure')
        with self.assertRaisesRegex(OSError, 'late failure'):
            stream.close()

    def test_measure_reports_deltas_peak_memory_and_state_only_queries(self):
        def point(requests, cpu, rss, failures=0):
            return {'peerRequests': requests, 'peerFailures': failures, 'cpuSeconds': cpu, 'rssBytes': rss}
        for active in (True, False):
            samples = [point(2, 1, 10)] * 2 + [point(3, 2, 30)] * 2 + [point(5, 4, 20)] * 2
            with patch.object(benchmark, 'Stream') as stream_type, patch.object(benchmark, 'metrics', side_effect=samples), patch.object(benchmark.time, 'sleep'), patch.object(benchmark.time, 'monotonic', side_effect=[100, 102]):
                stream_type.return_value.events = ['one', 'two']
                result = benchmark.measure(['http://node1/', 'http://node2'], 2, active)
            stream_type.assert_called_once_with('http://node1/status-functions-stream' + ('' if active else '?activity=false'))
            stream_type.return_value.close.assert_called_once()
            self.assertEqual(result, {'peerFailures': 0, 'view': 'Traffic' if active else 'Overview', 'seconds': 2,
                                      'peerRequests': 6, 'nodeCpuSeconds': 6, 'peakNodeRssBytes': 60, 'receivedEvents': 2})

    def test_measure_rejects_failed_peer_reads_and_always_closes_the_observer(self):
        before = {'peerRequests': 2, 'peerFailures': 0, 'cpuSeconds': 1, 'rssBytes': 10}
        after = {**before, 'peerRequests': 5, 'peerFailures': 3}
        for failure in (None, OSError('metrics unavailable')):
            with patch.object(benchmark, 'Stream') as stream_type, patch.object(benchmark, 'metrics', side_effect=[before, failure or after]), patch.object(benchmark.time, 'sleep'):
                with self.assertRaisesRegex(OSError if failure else RuntimeError, 'metrics unavailable' if failure else '3 peer reads failed'):
                    benchmark.measure(['http://node'], 1, True)
                stream_type.return_value.close.assert_called_once()

    def test_cli_validates_duration_and_emits_both_views_with_the_requested_label(self):
        output = io.StringIO()
        with patch.object(benchmark, 'measure', side_effect=[{'view': 'Traffic'}, {'view': 'Overview'}]) as measure, redirect_stdout(output):
            benchmark.main(['--nodes', 'http://node', '--seconds', '2', '--interval-label', '2000'])
        self.assertEqual([json.loads(line) for line in output.getvalue().splitlines()],
                         [{'configuredIntervalMs': '2000', 'view': 'Traffic'}, {'configuredIntervalMs': '2000', 'view': 'Overview'}])
        self.assertEqual([call.args for call in measure.call_args_list], [(['http://node'], 2, True), (['http://node'], 2, False)])
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
            benchmark.main(['--seconds', '0'])
        self.assertEqual(raised.exception.code, 2)


def executable_lines(code):
    """Use Python's compiled line table, including nested functions/generators."""
    lines = {line for _, line in dis.findlinestarts(code) if line is not None and line > 0}
    for constant in code.co_consts:
        if isinstance(constant, types.CodeType):
            lines.update(executable_lines(constant))
    return lines


def run_tests():
    spec.loader.exec_module(benchmark)
    return unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(BenchmarkTests))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--coverage', type=Path)
    args = parser.parse_args()
    tracer = trace.Trace(count=True, trace=False, ignoremods=['threading'])
    # trace also records work performed by Stream's reader threads.
    threading.settrace(tracer.globaltrace)
    try:
        result = tracer.runfunc(run_tests)
    finally:
        threading.settrace(None)
    if args.coverage:
        counts = tracer.results().counts
        lines = executable_lines(compile(SCRIPT.read_text(), str(SCRIPT), 'exec'))
        root = ET.Element('coverage', version='1')
        file = ET.SubElement(root, 'file', path='.bin/status-stream-benchmark.py')
        covered = 0
        for line in sorted(lines):
            hit = counts.get((str(SCRIPT), line), 0) > 0
            covered += int(hit)
            ET.SubElement(file, 'lineToCover', lineNumber=str(line), covered=str(hit).lower())
        ET.ElementTree(root).write(args.coverage, encoding='utf-8', xml_declaration=True)
        print(f'Benchmark executed-line coverage: {covered}/{len(lines)} ({covered / len(lines):.1%})')
        if covered / len(lines) < .8:
            raise SystemExit('Benchmark coverage is below 80%')
    raise SystemExit(0 if result.wasSuccessful() else 1)


if __name__ == '__main__':
    main()
