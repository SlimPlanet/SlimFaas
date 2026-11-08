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


def resources(path, phase=None):
    nodes, peaks, count = {}, [], 0
    with gzip.open(path, 'rt') as source:
        for line in source:
            record = json.loads(line)
            if phase and record.get('phase') != phase:
                continue
            cluster_rss = []
            for node in record['nodes']:
                if 'metrics' not in node:
                    continue
                count += 1
                values = {}
                for metric in node['metrics'].splitlines():
                    key, raw = metric.rsplit(' ', 1)
                    value = float(raw)
                    if math.isfinite(value):
                        values[key] = value
                history = nodes.setdefault(str(node['port']), {})
                for key, value in values.items():
                    previous = history.setdefault(key, {'first': value, 'last': value, 'minimum': value, 'maximum': value})
                    previous['last'] = value
                    previous['minimum'] = min(previous['minimum'], value)
                    previous['maximum'] = max(previous['maximum'], value)
                if 'slimdata_process_working_set_bytes' in values:
                    cluster_rss.append(values['slimdata_process_working_set_bytes'])
            if len(cluster_rss) == 3:
                peaks.append(sum(cluster_rss))
    def counter(prefix):
        rows = [v for fields in nodes.values() for k, v in fields.items() if k.startswith(prefix)]
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


native = []
for directory in ('paired-native-final', 'paired-native-confirmation'):
    base = RAW / directory
    for status in json.loads((base / 'status.json').read_text()):
        label = f"{status['ordinal']:02}-{status['variant']}"
        run = base / label
        if not (run / 'results.json').is_file():
            native.append(status | {'run': directory + '/' + label,
                'included_in_comparison': False, 'incomplete': True,
                'error': '1 MiB/concurrency 4 Async warmup queue did not drain within 180 seconds',
                'resources_whole_run': resources(base / (label + '-metrics.jsonl.gz')),
                'files': [file_id(p) for p in sorted(run.iterdir()) if p.is_file()]})
            continue
        report = json.loads((run / 'results.json').read_text())
        native.append(status | {'run': directory + '/' + label,
            'included_in_comparison': status['exit_code'] == 0,
            'settings': report['Settings'], 'latency': report['Latency'], 'scaling': report['Scaling'],
            'resources_whole_run': resources(base / (label + '-metrics.jsonl.gz')),
            'files': [file_id(p) for p in sorted(run.iterdir()) if p.is_file()]})
memory = []
base = RAW / 'paired-memory-lab-final'
for run in sorted(base.iterdir()):
    if not run.is_dir():
        continue
    parameters = json.loads((run / 'parameters.json').read_text())
    loads = []
    for path in sorted(run.glob('*-measure.json')):
        label = path.stem
        loads.append({'report': json.loads(path.read_text())['report'],
                      'resources': resources(run / 'metrics.jsonl.gz', label),
                      'files': [file_id(path), file_id(path.with_suffix('.csv')),
                                file_id(path.with_suffix('.log'))]})
    memory.append({'run': run.name, 'parameters': parameters, 'loads': loads,
                   'async_reconciliation': json.loads((run / 'async-reconciliation.json').read_text()),
                   'metrics': file_id(run / 'metrics.jsonl.gz')})
comparisons = []
for ordinal in (1, 4):
    source = RAW / f'native-comparison-{ordinal}/comparison.json'
    data = json.loads(source.read_text())
    name = f'native-comparison-{ordinal}.json'
    shutil.copy2(source, OUT / name)
    row = next(x for x in data['Sync'] if x['PayloadBytes'] == 2097152 and x['Concurrency'] == 4)
    comparisons.append({'json': name, 'raw_file': file_id(source), 'exit_code': 1,
        'optimization_criteria_passed': data['Passed'], 'sync_guardrails_passed': data['SyncGuardrailsPassed'],
        'two_mib_c4_normalized_rate_change_percent': row['RateRatioChangePercent']})
evidence = {'schema': 1, 'host': {'os': 'Windows 10.0.26300', 'cpu': 'AMD Ryzen 5 3500U',
    'logical_processors': 8, 'physical_ram_bytes': 14977466368,
    'runtime': '.NET 10.0.14', 'sdk': '10.0.403'},
    'native': native, 'memory_lab': memory, 'existing_comparer': comparisons,
    'failed_native_state': file_id(RAW / 'native-confirmation-failed-state.tar.gz') | {
        'logical_files': 197070, 'logical_bytes': 815372782,
        'note': 'Whole retained failed state including logs; not an allocated-block or wire-bandwidth measurement'},
    'excluded_memory_lab_setup': {
        'reason': 'The first manifest omitted the existing driver hardcoded memory-function; mixed Async returned 404',
        'path': 'artifacts/slimdata-lts/paired-memory-lab',
        'kv_validation_failures': 0, 'mixed_async_http_404': 10,
        'failure_log': file_id(RAW / 'paired-memory-lab/01-baseline/slimdata-mixed-c12-warmup.log')},
    'limitations': ['screening, two baseline runs and one successful candidate run, shared developer host',
        'ABBA attempted; case 2 excluded after post-driver Bash read error caused by editing the script',
        'replacement candidate failed its 180-second Async warmup queue drain; original failure retained',
        'metric deltas are sampled/gauge-derived and can omit boundary work',
        'no measured wire replication byte throughput', 'percentile columns are medians of run percentiles']}
(OUT / 'performance.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')

lines = ['# Native performance screening — 8 November 2026', '',
    'See [the reliability verdict](../../slimdata-reliability-results.md). '
    'This short same-host comparison cannot certify non-regression.', '',
    'Baseline v0.88.0 and candidate fixes 1–10 use the same existing driver/manifest. '
    'Five-second measurements follow two-second warmups. Six sizes and concurrency '
    '1/4 are tested; the scaling burst uses 200 messages/concurrency 32. '
    'No local compilation or other workload campaign ran concurrently.', '',
    'ABBA was attempted. The second run completed its driver and scaling, but editing '
    'the launcher while Bash was reading it caused a post-driver syntax error. '
    'That run is excluded from the tables. A replacement candidate failed the '
    '180-second queue-drain budget after Async warmup at 1 MiB/concurrency four. '
    'Its remaining cases and scaling were not run. '
    'The earlier contaminated comparison is also excluded; all failures are retained.', '',
    'Values below use **two baseline runs and one complete candidate run**. '
    'They are medians of the available per-run values, including the percentile '
    'columns; they are not percentiles pooled across runs. Errors/missing/duplicates '
    'are totals. Raw results and file hashes are in [performance.json](performance.json).', '']
good = [x for x in native if x['included_in_comparison']]
for mode in ('sync', 'async'):
    lines += ['## ' + ('Sync response' if mode == 'sync' else 'Async HTTP acceptance'), '',
        '| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |',
        '|---:|---:|---:|---:|---:|---:|---:|']
    for size in (65536,262144,1048576,2000000,2097152,4194304):
        for concurrency in (1,4):
            rows = {variant: [r for run in good if run['variant'] == variant for r in run['latency']
                    if (r['Mode'],r['PayloadBytes'],r['Concurrency']) == (mode,size,concurrency)]
                    for variant in ('baseline','candidate')}
            assert len(rows['baseline']) == 2 and len(rows['candidate']) == 1, (mode,size,concurrency)
            rates = [median(r['MeanRatePerSecond'] for r in rows[v]) for v in ('baseline','candidate')]
            pct = ['/'.join(f"{median(r[k] for r in rows[v]):.2f}" for k in
                   ('P50Milliseconds','P95Milliseconds','P99Milliseconds')) for v in ('baseline','candidate')]
            lines.append(f'| {size:,} | {concurrency} | {rates[0]:.2f} | {rates[1]:.2f} | '
                         f'{(rates[1]/rates[0]-1)*100:+.1f}% | {pct[0]} | {pct[1]} |')
    lines += ['']
lines += ['## Async sampled resources by body size', '',
    'Concurrency four; three runtime processes combined. CPU/message and sampled '
    'maximum RSS come from the existing driver and retain its sampling limitations.', '',
    '| Bytes | Base CPU ms/message | Candidate CPU ms/message | Base sampled max RSS MiB | Candidate sampled max RSS MiB |',
    '|---:|---:|---:|---:|---:|']
for size in (65536,262144,1048576,2000000,2097152,4194304):
    rows = {v: [r for run in good if run['variant'] == v for r in run['latency']
                if (r['Mode'],r['PayloadBytes'],r['Concurrency']) == ('async',size,4)]
            for v in ('baseline','candidate')}
    values = []
    for key, scale in (('AsyncCpuMillisecondsPerMessage',1),('AsyncPeakWorkingSetBytes',2**20)):
        for variant in ('baseline','candidate'):
            available = [r[key] for r in rows[variant] if r[key] is not None]
            values.append(f'{median(available)/scale:.2f}' if available else 'unavailable')
    lines.append(f'| {size:,} | ' + ' | '.join(values) + ' |')
lines += ['', '## Scaling milestones', '',
    '| Run | First ready replica ms | Four ready replicas ms | Queue drained ms |',
    '|---|---:|---:|---:|']
for run in good:
    r=run['scaling']
    lines.append(f"| {run['run']} | {r['ReadyOneMilliseconds']:.1f} | {r['ReadyTargetMilliseconds']:.1f} | {r['QueueDrainedMilliseconds']:.1f} |")
lines += ['', '## Correctness and resources', '']
for run in native:
    if run.get('incomplete'):
        lines.append(f"- `{run['run']}`: wrapper exit {run['exit_code']}; **failed**, {run['error']}. "
            'Partial raw CSV and metrics retained; no final JSON or successful scaling verdict.')
        continue
    rows = run['latency']
    lines.append(f"- `{run['run']}`: wrapper exit {run['exit_code']}; "
        f"{sum(x['Failed'] for x in rows)} HTTP errors, "
        f"{sum(x['AsyncMissing'] for x in rows)} missing Async, "
        f"{sum(x['AsyncDuplicates'] for x in rows)} duplicate Async; "
        f"scaling failed={run['scaling']['Failed']}, timed_out={run['scaling']['TimedOut']}.")
lines += ['', '| Variant/run | Cluster peak RSS MiB | CPU seconds | Allocated GiB | GC collections | GC pause seconds | Max node WAL since snapshot MiB |',
          '|---|---:|---:|---:|---:|---:|---:|']
for run in good:
    r=run['resources_whole_run']
    lines.append(f"| {run['run']} | {r['peak_cluster_working_set_bytes']/2**20:.1f} | {r['cpu_seconds_delta']:.1f} | "
        f"{r['allocated_bytes_delta']/2**30:.2f} | {r['gc_collections_delta']:.0f} | {r['gc_pause_seconds_delta']:.3f} | "
        f"{r['maximum_node_wal_bytes_since_snapshot']/2**20:.1f} |")
lines += ['', 'These whole-run metrics include warmups, draining and scaling, whose durations differ. '
    'They cannot be interpreted as normalized cost per request. Per-size Async CPU/message, '
    'peak working set, Raft entries/message and actual function-arrival percentiles are '
    'preserved in the JSON. Gauges update at a coarser cadence than some short phases. '
    'CPU/RSS cover the three runtime processes, excluding the driver and function processes. '
    'The failed state archive retains 197,070 files / 815,372,782 logical bytes, including logs; '
    'this is a final footprint observation, not a measured growth rate or root cause.', '',
    '## Existing MemoryLab data workloads', '',
    'Each binary has two real three-node persistent Local runs. Each scenario uses '
    '4 KiB values, 16 keys/worker, concurrency 12/48, two-second warmup and ten-second '
    'measurement, with `--validate true`. This is a separate sequential comparison.', '',
    'The initial setup attempt is excluded: the existing driver hardcodes '
    '`memory-function`, which was missing from that first manifest. It returned '
    'ten Async 404s while KV validation reported no errors. The corrected fresh '
    'comparison declares that real function, verifies every distinct delivered '
    'Async element ID and its 4 KiB checksum, and retains original failures.', '',
    '| Scenario | C | Base ops/s | Candidate ops/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms | Errors base/candidate |',
    '|---|---:|---:|---:|---:|---:|---:|---:|']
for scenario in ('slimdata-set','slimdata-mixed'):
    for concurrency in (12,48):
        rows = {v:[load['report'] for run in memory if run['parameters']['variant']==v
                for load in run['loads'] if load['report']['Scenario']==scenario
                and load['report']['Concurrency']==concurrency] for v in ('baseline','candidate')}
        assert all(len(v)==2 for v in rows.values())
        rates=[median(r['Rate'] for r in rows[v]) for v in ('baseline','candidate')]
        def overall(r):
            return next(x for x in r['Operations'] if x['Operation']=='All')
        pct=['/'.join(f"{median(overall(r)[k] for r in rows[v]):.2f}" for k in
            ('P50Milliseconds','P95Milliseconds','P99Milliseconds')) for v in ('baseline','candidate')]
        failures=[sum(r['Failed'] for r in rows[v]) for v in ('baseline','candidate')]
        lines.append(f'| {scenario} | {concurrency} | {rates[0]:.2f} | {rates[1]:.2f} | '
            f'{(rates[1]/rates[0]-1)*100:+.1f}% | {pct[0]} | {pct[1]} | {failures[0]}/{failures[1]} |')
lines += ['', 'Raw CSV/JSON/logs, runtime/driver hashes and per-phase CPU/RAM/allocation/GC '
    'and WAL/snapshot samples are retained. `performance.json` indexes each measured '
    'load and its sampled resources. Phase resource windows also include driver validation. '
    'Wire replication bandwidth was **not measured**. '
    'Neither byte payload rate nor a resettable WAL gauge is a substitute for it.', '',
    'These small unequal Native samples and the two-per-binary MemoryLab repetitions '
    'on a shared developer laptop show substantial variance; individual '
    'tail regressions remain visible. These measurements do not establish a performance '
    'non-regression guarantee. Longer controlled repeats and Windows large-Stream '
    'availability must be resolved before LTS acceptance.', '']
lines += ['## Existing normalized comparison', '',
    'The existing `SlimFaasBenchmark compare` command was also run against each '
    'baseline and the complete candidate. Both return exit 1. Its optimization '
    'targets additionally demand a latency improvement, which is not this reliability '
    'change\'s objective; importantly, **its non-regression guardrails also fail**.', '',
    '| Baseline | Sync 2 MiB / C4 change in proxy-to-direct throughput ratio | Guardrails |',
    '|---|---:|---|']
for comparison in comparisons:
    lines.append(f"| [{comparison['json']}]({comparison['json']}) | "
        f"{comparison['two_mib_c4_normalized_rate_change_percent']:+.1f}% | Failed |")
lines += ['', 'These observed regressions are not waived because the samples are short or noisy. '
    'They require investigation and controlled repeats, alongside the separate '
    '180-second queue-drain failure. No causal attribution to a particular fix '
    'or to AOT alone is established.', '']
(OUT / 'performance.md').write_text('\n'.join(lines), encoding='utf-8')
print('Wrote performance evidence')
