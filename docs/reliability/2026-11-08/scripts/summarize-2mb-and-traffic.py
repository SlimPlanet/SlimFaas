import hashlib
import json
from pathlib import Path
from statistics import median

root = Path.cwd()
raw = root / 'artifacts/slimdata-lts'
out = root / 'docs/reliability/2026-11-08'
def identity(path):
    return {'path': path.relative_to(root).as_posix(), 'bytes': path.stat().st_size,
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
base = raw / 'paired-native-2mb-a95'
statuses = json.loads((base / 'status.json').read_text())
assert len(statuses) == 4 and all(row['exit_code'] == 0 for row in statuses)
runs = []
for status in statuses:
    label = f"{status['ordinal']:02}-{status['variant']}"
    directory = base / label
    runs.append(status | {'label': label, 'report': json.loads((directory / 'results.json').read_text()),
        'attribution': json.loads((directory / 'runtime-attribution.json').read_text()),
        'files': [identity(path) for path in sorted(directory.iterdir()) if path.is_file()]})
comparisons = []
for directory in sorted(raw.glob('compare-2mb-a95-*')):
    if not directory.is_dir():
        continue
    data = json.loads((directory / 'comparison.json').read_text())
    comparisons.append({'name': directory.name, 'report': data,
                        'raw_report': identity(directory / 'comparison.json')})
document = {'schema': 1, 'runs': runs, 'existing_comparer': comparisons,
    'note': 'Separate same-host ABBA targeting exactly 2,000,000 and 2,097,152 bytes; c1/4, two-second warmup and ten-second measurements. All four runs pass. Full-profile baseline failures remain in performance-current.json. Percentile columns are medians of per-run percentiles.'}
with (out / 'performance-2mb.json').open('x') as target:
    json.dump(document, target, indent=2)
lines = ['# Targeted 2 MB / 2 MiB Native comparison', '',
    'This separate ABBA uses the existing Local driver unchanged, with the same settings for baseline '
    '`496e8739` and candidate `a95d49e8`: concurrency 1/4, two-second warmups, ten-second measurements '
    'and the original 200-message scaling burst. All four runs complete. The two full-profile baseline '
    '180-second queue-drain failures remain in [performance-current.md](performance-current.md). '
    'This targeted experiment does not replace those failed workloads.', '',
    '## Throughput and latency', '',
    '| Mode | Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |',
    '|---|---:|---:|---:|---:|---:|---:|---:|']
for mode in ('sync', 'async'):
    for size in (2000000, 2097152):
        for concurrency in (1, 4):
            rows = {kind: [case for run in runs if run['variant'] == kind for case in run['report']['Latency']
                          if (case['Mode'], case['PayloadBytes'], case['Concurrency']) == (mode, size, concurrency)]
                    for kind in ('baseline', 'candidate')}
            rates = [median(row['MeanRatePerSecond'] for row in rows[kind]) for kind in ('baseline', 'candidate')]
            pct = ['/'.join(f"{median(row[key] for row in rows[kind]):.2f}" for key in
                    ('P50Milliseconds', 'P95Milliseconds', 'P99Milliseconds')) for kind in ('baseline', 'candidate')]
            lines.append(f'| {mode} | {size:,} | {concurrency} | {rates[0]:.2f} | {rates[1]:.2f} | '
                         f'{100*(rates[1]/rates[0]-1):+.1f}% | {pct[0]} | {pct[1]} |')
lines += ['', '## Existing normalized comparison', '',
    'The existing comparer evaluates each candidate against both bracketing baselines. Its optimization '
    'target of 40% large-payload p50 reduction is separate from its 10% Sync non-regression guardrails. '
    'All candidates have no request failures. Both candidates fail Sync guardrails against baseline 01 '
    'and pass them against baseline 04. This observed variability does not certify non-regression.', '',
    '| Baseline | Candidate | Sync guardrails | 2 MiB/C4 normalized rate change | Added p95 / p99 change |',
    '|---|---|---|---:|---:|']
for item in comparisons:
    report = item['report']
    case = next(row for row in report['Sync'] if row['PayloadBytes'] == 2097152 and row['Concurrency'] == 4)
    lines.append(f"| {Path(report['BaselinePath']).parent.name} | {Path(report['CandidatePath']).parent.name} | "
        f"{report['SyncGuardrailsPassed']} | {case['RateRatioChangePercent']:+.2f}% | "
        f"{case['AddedP95ChangePercent']:+.1f}% / {case['AddedP99ChangePercent']:+.1f}% |")
lines += ['', 'Added latency subtracts the direct target percentile. A small baseline difference can '
    'produce a large percentage; inspect the absolute raw fields before interpreting these tail ratios. '
    'The complete reports, actual samples, CPU/message, RSS, Raft-entry observations, failure/duplicate '
    'counts and hashes are in [performance-2mb.json](performance-2mb.json). The cause of the variability '
    'is not established and the remaining performance gate is explicit.', '', '## Scaling', '',
    '| Run | First ready ms | Four ready ms | Queue drained ms |', '|---|---:|---:|---:|']
for run in runs:
    value = run['report']['Scaling']
    lines.append(f"| {run['label']} | {value['ReadyOneMilliseconds']:.1f} | "
                 f"{value['ReadyTargetMilliseconds']:.1f} | {value['QueueDrainedMilliseconds']:.1f} |")
with (out / 'performance-2mb.md').open('x') as target:
    target.write('\n'.join(lines) + '\n')
linux = json.loads((raw / 'final-post-run-linux.json').read_text())
with (out / 'linux-retained-replay-and-traffic.json').open('x') as target:
    json.dump(linux, target, indent=2)
lines = ['# Observed Raft-listener TCP traffic', '',
    'Real three-node Linux Local, proper framework-dependent publications, identical SDK/fixture '
    'and original ABBA order. Each run publishes 24 real HTTP Async messages at concurrency four: '
    'six exact body sizes repeated with Content-Length and chunked encoding, totaling **38,670,848 '
    'application bytes**. Every accepted body reaches the real function with its checksum verified.', '',
    'AF_PACKET captures IPv4 TCP on loopback, using only PACKET_OUTGOING to avoid counting each '
    'loopback copy twice. Selected ports are the three Raft listeners. Counts include internal HTTP '
    'framing, control traffic, snapshots and retransmissions. They are observed TCP payload bytes, '
    'not physical-wire bandwidth or maximum steady-state replication capacity. Duration includes '
    'startup, the workload, shutdown and the capture drain.', '',
    '## Original capture attempts', '',
    'All four first workloads pass, but baseline 01 loses 315 capture packets and candidate 03 '
    'loses 113. Those byte measurements are excluded. A new complete ABBA uses a 128 MiB forced '
    'socket buffer (kernel-reported 256 MiB). Its exact-byte/other-port self-test passes and '
    '**all four workloads and all four zero-drop capture gates pass**.', '',
    '| Run | Seconds | TCP payload bytes | Observed MiB/s | Kernel drops |', '|---|---:|---:|---:|---:|']
attempt = next(row for row in linux['traffic_attempts'] if row['name'] == 'traffic-4e80-buffered')
for run in attempt['runs']:
    capture = run['capture']
    assert capture['kernel_drops'] == 0 and run['workload']['reports']['report.json']['passed']
    lines.append(f"| {run['label']} | {capture['seconds']:.3f} | {capture['counts']['tcp_payload_bytes']:,} | "
                 f"{capture['tcp_payload_bytes_per_second']/2**20:.3f} | 0 |")
lines += ['', 'Per-second counters, to/from-listener byte counts, runtime/harness hashes, client '
    'ledgers, receipt reconciliation, logical WAL sizes and all failed first captures are indexed in '
    '[linux-retained-replay-and-traffic.json](linux-retained-replay-and-traffic.json). Raw original '
    'volumes remain in the owned WSL cache. WAL logical file sizes and snapshot gauges are separate '
    'from captured network bytes; neither proves allocated physical disk growth.', '']
with (out / 'replication-traffic.md').open('x') as target:
    target.write('\n'.join(lines))
