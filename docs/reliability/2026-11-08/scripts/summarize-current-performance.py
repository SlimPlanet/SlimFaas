import gzip
import hashlib
import json
import math
import shutil
from pathlib import Path
from statistics import median

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / 'artifacts/slimdata-lts'
OUT = ROOT / 'docs/reliability/2026-11-08'


def file_id(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return {'path': str(path.relative_to(ROOT)).replace('\\', '/'),
            'bytes': path.stat().st_size, 'sha256': digest.hexdigest()}


def metric_values(text):
    values = {}
    for metric in text.splitlines():
        key, raw = metric.rsplit(' ', 1)
        value = float(raw)
        if math.isfinite(value):
            values[key] = value
    return values


def update_history(history, values):
    for key, value in values.items():
        previous = history.setdefault(key, {'first': value, 'last': value, 'minimum': value, 'maximum': value})
        if value < previous['last']:
            previous['decreases'] = previous.get('decreases', 0) + 1
        previous['last'] = value
        previous['minimum'] = min(previous['minimum'], value)
        previous['maximum'] = max(previous['maximum'], value)


def record_resources(record, histories):
    cluster_rss, count = [], 0
    for node in record['nodes']:
        if 'metrics' not in node:
            continue
        count += 1
        values = metric_values(node['metrics'])
        update_history(histories.setdefault(str(node['port']), {}), values)
        if 'slimdata_process_working_set_bytes' in values:
            cluster_rss.append(values['slimdata_process_working_set_bytes'])
    return count, sum(cluster_rss) if len(cluster_rss) == 3 else None


def resources(path, phase=None):
    nodes, peaks, count = {}, [], 0
    with gzip.open(path, 'rt') as source:
        for line in source:
            record = json.loads(line)
            if phase and record.get('phase') != phase:
                continue
            samples, peak = record_resources(record, nodes)
            count += samples
            if peak is not None:
                peaks.append(peak)
    def counter(prefix):
        rows = [v for fields in nodes.values() for k, v in fields.items() if k.startswith(prefix)]
        if any(row.get('decreases', 0) for row in rows):
            return None
        return sum(v['last'] - v['first'] for v in rows) if rows else None
    def maximum(prefix):
        rows = [v['maximum'] for fields in nodes.values() for k, v in fields.items() if k.startswith(prefix)]
        return max(rows) if rows else None
    return {'node_samples': count, 'peak_cluster_working_set_bytes': max(peaks) if peaks else None,
            'cpu_seconds_delta': counter('slimdata_process_cpu_seconds_total'),
            'allocated_bytes_delta': counter('system_runtime_dotnet_gc_heap_total_allocated'),
            'gc_collections_delta': counter('system_runtime_dotnet_gc_collections'),
            'gc_pause_seconds_delta': counter('system_runtime_dotnet_gc_pause_time'),
            'maximum_node_wal_bytes_since_snapshot': maximum('slimdata_wal_bytes_since_snapshot'),
            'maximum_node_last_snapshot_bytes': maximum('slimdata_snapshot_last_size_bytes'),
            'format_violation_maximum': maximum('slimdata_raft_command_format_violation_total'),
            'size_violation_maximum': maximum('slimdata_raft_command_size_violation_total')}


native_root = RAW / 'paired-native-a95'
statuses = json.loads((native_root / 'status.json').read_text())
assert len(statuses) == 4, 'All four original ABBA attempts must finish before summarizing'
native = []
for status in statuses:
    label = f"{status['ordinal']:02}-{status['variant']}"
    run = native_root / label
    report_path = run / 'results.json'
    report = json.loads(report_path.read_text()) if report_path.is_file() else None
    native.append(status | {'run': label, 'complete': report is not None,
        'included_in_complete_run_tables': report is not None and status['exit_code'] == 0,
        'report': report, 'attribution': json.loads((run / 'runtime-attribution.json').read_text()),
        'resources_whole_run': resources(native_root / (label + '-metrics.jsonl.gz')),
        'files': [file_id(path) for path in sorted(run.iterdir()) if path.is_file()]})
memory_root = RAW / 'paired-memory-lab-a95'
memory_status = json.loads((memory_root / 'status.json').read_text())
assert len(memory_status) == 32 and all(row['exit_code'] == 0 for row in memory_status)
memory = []
for run in sorted(path for path in memory_root.iterdir() if path.is_dir()):
    memory.append({'run': run.name, 'parameters': json.loads((run / 'parameters.json').read_text()),
        'loads': [{'report': json.loads(path.read_text())['report'],
                   'resources': resources(run / 'metrics.jsonl.gz', path.stem), 'raw_report': file_id(path)}
                  for path in sorted(run.glob('*-measure.json'))],
        'async_reconciliation': json.loads((run / 'async-reconciliation.json').read_text()),
        'metrics': file_id(run / 'metrics.jsonl.gz')})
evidence = {'schema': 1, 'baseline_commit': '496e8739e5f7b4326f64520fe4caa4e0e23f8d06',
    'candidate_runtime_commit': 'a95d49e8cd48c433e482a724e5f0c77c7ea29eb5',
    'http': native, 'memory_lab': memory,
    'memory_lab_status': memory_status,
    'limitations': ['Same shared Windows developer host; ten-second screening, two-second warmup',
        'Every original ABBA attempt retained; partial runs excluded only from complete-run tables',
        'Medians of per-run percentile columns are not pooled percentiles',
        'Sampled gauge-derived resource deltas omit boundary work; missing/reset counters remain null',
        'WAL-since-snapshot and snapshot-size gauges are not allocated disk growth or wire throughput',
        'Replication TCP observations are reported in a separate instrumented comparison']}
with (OUT / 'performance-current.json').open('x', encoding='utf-8') as output:
    json.dump(evidence, output, indent=2)
good = [row for row in native if row['included_in_complete_run_tables']]
counts = {kind: sum(row['variant'] == kind for row in good) for kind in ('baseline', 'candidate')}
lines = ['# Final runtime performance screening — 9 November 2026', '',
    'Read [the reliability verdict](../../slimdata-reliability-results.md). Baseline `496e8739` and '
    'Native candidate `a95d49e8` use the existing Local driver in original ABBA order. '
    'Measurements last ten seconds after two-second warmups; six exact body sizes and concurrency 1/4 '
    'are followed by a 200-message scaling burst. Compilation and other workload campaigns were stopped.', '',
    f"Complete-run tables contain **{counts['baseline']} baseline and {counts['candidate']} candidate runs**. "
    'Every failed original attempt remains below and in [the raw evidence index](performance-current.json). '
    'No shorter deadline, mutation retry or replacement run turns a failure into a pass. '
    'Percentile columns are medians of per-run percentiles, not pooled percentiles.', '',
    '## Original HTTP/scaling attempts', '', '| Run | Exit | Complete report | Queue drained ms |',
    '|---|---:|---|---:|']
for row in native:
    scaling = row['report']['Scaling'] if row['report'] else None
    lines.append(f"| {row['run']} | {row['exit_code']} | {row['complete']} | " +
                 (f"{scaling['QueueDrainedMilliseconds']:.1f}" if scaling else 'not reached') + ' |')
lines += ['', 'A partial run without `results.json` is not a complete performance baseline. Its logs, '
    'manifest, runtime attribution and metric stream remain retained. Inspect the original exception '
    'before attributing a failure to a particular defect.', '']
def middle(rows, field):
    values = [row[field] for row in rows if row.get(field) is not None]
    return median(values) if values else None
def number(value, digits=2):
    return '—' if value is None else f'{value:.{digits}f}'
for mode in ('sync', 'async'):
    lines += ['## ' + ('Sync response' if mode == 'sync' else 'Async HTTP acceptance'), '',
        '| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |',
        '|---:|---:|---:|---:|---:|---:|---:|']
    for size in (65536, 262144, 1048576, 2000000, 2097152, 4194304):
        for concurrency in (1, 4):
            rows = {kind: [case for run in good if run['variant'] == kind for case in run['report']['Latency']
                if (case['Mode'], case['PayloadBytes'], case['Concurrency']) == (mode, size, concurrency)]
                for kind in ('baseline', 'candidate')}
            rates = [middle(rows[kind], 'MeanRatePerSecond') for kind in ('baseline', 'candidate')]
            change = None if rates[0] is None else 100 * (rates[1] / rates[0] - 1)
            percentiles = ['/'.join(number(middle(rows[kind], field)) for field in
                ('P50Milliseconds', 'P95Milliseconds', 'P99Milliseconds')) for kind in ('baseline', 'candidate')]
            change_text = '—' if change is None else f'{change:+.1f}%'
            lines.append(f'| {size:,} | {concurrency} | {number(rates[0])} | {number(rates[1])} | '
                f'{change_text} | {percentiles[0]} | {percentiles[1]} |')
    lines += ['']
lines += ['## Async delivery at concurrency four', '',
    'Arrival and completion percentiles use the existing real target observations. Missing/duplicate '
    'counts are totals across complete runs; percentile columns are medians of per-run values.', '',
    '| Bytes | Variant | Arrival p95 ms | Completion p95/p99 ms | Observed | Missing | Duplicates |',
    '|---:|---|---:|---:|---:|---:|---:|']
for size in (65536, 262144, 1048576, 2000000, 2097152, 4194304):
    for kind in ('baseline', 'candidate'):
        rows = [case for run in good if run['variant'] == kind for case in run['report']['Latency']
                if (case['Mode'], case['PayloadBytes'], case['Concurrency']) == ('async', size, 4)]
        counts_text = [str(sum(row[key] or 0 for row in rows)) if rows else '—'
                       for key in ('AsyncObserved', 'AsyncMissing', 'AsyncDuplicates')]
        lines.append(f'| {size:,} | {kind} | {number(middle(rows, "AsyncArrivalP95Milliseconds"))} | '
            f'{number(middle(rows, "AsyncCompletionP95Milliseconds"))}/'
            f'{number(middle(rows, "AsyncCompletionP99Milliseconds"))} | ' + ' | '.join(counts_text) + ' |')
lines += ['']
lines += ['## MemoryLab validation and throughput', '',
    'All 16 measured loads and 16 warmups completed with zero failures. Every phase validates '
    'the stored values and reconciles distinct actual Async delivery IDs and 4 KiB checksums. '
    'The same three-node Local configuration forces snapshots every 50 entries or 8 MiB.', '',
    '| Scenario | C | Base ops/s | Candidate ops/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |',
    '|---|---:|---:|---:|---:|---:|---:|']
for scenario in ('slimdata-set', 'slimdata-mixed'):
    for concurrency in (12, 48):
        rows = {kind: [load['report'] for run in memory if kind in run['run'] for load in run['loads']
                if load['report']['Scenario'] == scenario and load['report']['Concurrency'] == concurrency]
                for kind in ('baseline', 'candidate')}
        rates = [middle(rows[kind], 'Rate') for kind in ('baseline', 'candidate')]
        pct = ['/'.join(number(middle([row['Operations'][0] for row in rows[kind]], field))
            for field in ('P50Milliseconds', 'P95Milliseconds', 'P99Milliseconds')) for kind in ('baseline', 'candidate')]
        lines.append(f'| {scenario} | {concurrency} | {rates[0]:.2f} | {rates[1]:.2f} | '
            f'{100 * (rates[1] / rates[0] - 1):+.1f}% | {pct[0]} | {pct[1]} |')
lines += ['', '## Sampled whole-run Native resources', '',
    'Three runtime processes combined. CPU/allocation/GC are sampled counter deltas, '
    'with missing or reset counters left unmeasured. Whole-run values include startup and scaling '
    'and cannot be normalized as equal-work cost when a run fails or completes a different workload.', '',
    '| Run | Peak combined RSS MiB | CPU seconds | Allocated MiB | GC collections | GC pause seconds | Max node WAL-since-snapshot MiB | Max node last snapshot MiB |',
    '|---|---:|---:|---:|---:|---:|---:|---:|']
for row in native:
    values = row['resources_whole_run']
    def scaled(key):
        value = values[key]
        return number(value / 2 ** 20) if value is not None else '—'
    lines.append(f"| {row['run']} | {scaled('peak_cluster_working_set_bytes')} | "
        f"{number(values['cpu_seconds_delta'])} | {scaled('allocated_bytes_delta')} | "
        f"{number(values['gc_collections_delta'], 0)} | {number(values['gc_pause_seconds_delta'])} | "
        f"{scaled('maximum_node_wal_bytes_since_snapshot')} | {scaled('maximum_node_last_snapshot_bytes')} |")
lines += ['', 'Per-phase MemoryLab resource samples, all raw field values, complete latency/scaling reports '
    'and content hashes are in `performance-current.json`. Short sampled windows can miss boundary work; '
    'an absent measurement is not zero cost. Original historical five-second comparisons remain '
    'in [performance.md](performance.md), with their failures and different runtime revisions.', '']
with (OUT / 'performance-current.md').open('x', encoding='utf-8') as output:
    output.write('\n'.join(lines))
