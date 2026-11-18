"""Retain every managed micro observation and all four pair ratios; no application verdict."""
from pathlib import Path
import csv
import hashlib
import json
import statistics
import time

R = Path.cwd()
E = R / 'artifacts/pr506'
P = E / 'preflight/net11-156-micro-abba-analysis.json'
assert not P.exists()
source = json.loads((E / 'preflight/net11-common-micro-sources.json').read_text())
assert all(hashlib.sha256((R / name).read_bytes()).hexdigest() == digest
           for name, digest in source['files'].items())
runs = []
raw_files = []
for ordinal, variant in enumerate(['before', 'after', 'after', 'before'], 1):
    base = E / f'measurements/micro-net11-189-{ordinal}-{variant}'
    execution = E / f'logs/net11-96-isolated-{base.name}.execution.json'
    result = json.loads(execution.read_text())
    assert result['exit_code'] == 0 and result['isolation_valid'] and not result['external_activity']
    cases = {}
    host = None
    for report in sorted((base / 'results').glob('*full-compressed.json')):
        document = json.loads(report.read_text())
        host = document['HostEnvironmentInfo'] if host is None else host
        assert document['HostEnvironmentInfo'] == host
        for case in document['Benchmarks']:
            assert case['FullName'] not in cases and case['Statistics']['N'] == 3
            cases[case['FullName']] = dict(statistics_nanoseconds=case['Statistics'],
                allocated_bytes_per_operation=case['Memory']['BytesAllocatedPerOperation'])
    assert len(cases) == 74
    activity = execution.with_name(execution.name.replace('.execution.json', '.activity.jsonl'))
    samples = [json.loads(line) for line in activity.read_text().splitlines()]
    assert samples and all(not row['external_compilers_or_tests'] for row in samples)
    runs.append(dict(ordinal=ordinal, variant=variant, host=host, execution=result,
                     activity_samples=len(samples), maximum_sample_gap_seconds=max(
                         (b['utc']-a['utc'] for a,b in zip(samples,samples[1:])), default=0), cases=cases))
    for file in sorted(base.rglob('*')):
        if file.is_file():
            raw_files.append(dict(path=str(file.relative_to(E)), bytes=file.stat().st_size,
                                  sha256=hashlib.sha256(file.read_bytes()).hexdigest()))
    for file in [execution, activity]:
        raw_files.append(dict(path=str(file.relative_to(E)), bytes=file.stat().st_size,
                              sha256=hashlib.sha256(file.read_bytes()).hexdigest()))
assert all(set(run['cases']) == set(runs[0]['cases']) for run in runs)
rows = []
for name in sorted(runs[0]['cases']):
    means = [run['cases'][name]['statistics_nanoseconds']['Mean'] for run in runs]
    allocations = [run['cases'][name]['allocated_bytes_per_operation'] for run in runs]
    ratios = {f'a{a+1}-b{b+1}': means[b] / means[a] for a in [0, 3] for b in [1, 2]}
    rows.append(dict(case=name, reference_mean_ns=[means[0], means[3]],
        candidate_mean_ns=[means[1], means[2]],
        latency_ratio=statistics.mean(means[1:3])/statistics.mean([means[0], means[3]]),
        pair_latency_ratios=ratios, minimum_pair_latency_ratio=min(ratios.values()),
        maximum_pair_latency_ratio=max(ratios.values()),
        reference_allocated_bytes=[allocations[0], allocations[3]],
        candidate_allocated_bytes=allocations[1:3]))
result = dict(created_utc=time.time(), runtime_source='189826231fe43fe3069e8a1f7724f83589432c6f',
    reference_source='f36084643a75d803051a6918d2950d1a4f674ada',
    driver_proof='preflight/net11-144-managed-micro-driver.json',
    identical_driver_sources=source, case_count=74, completed_case_executions=296,
    execution_and_isolation_complete=True,
    scope='Managed .NET11 ARM64 ShortRunInProcess: descriptive means/allocations, three measurements per case. Seven consistently slower >1.10 cases remain open optimization leads. No Native AOT/application non-regression or statistical-significance claim. No repeated measurement or result selection.',
    consistently_over_110_percent=[r['case'] for r in rows if r['minimum_pair_latency_ratio']>1.1],
    consistently_under_90_percent=[r['case'] for r in rows if r['maximum_pair_latency_ratio']<.9],
    allocation_increase_cases=[r['case'] for r in rows if max(r['candidate_allocated_bytes'])>max(r['reference_allocated_bytes'])],
    runs=runs, comparisons=rows, raw_files=raw_files)
P.write_text(json.dumps(result, indent=2)+'\n')
csv_path=P.with_suffix('.csv')
with csv_path.open('w',newline='') as stream:
    fields=['case','reference_1_ns','candidate_2_ns','candidate_3_ns','reference_4_ns','latency_ratio',
            'a1-b2','a1-b3','a4-b2','a4-b3','reference_1_bytes','candidate_2_bytes','candidate_3_bytes','reference_4_bytes']
    writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
    for row in rows:
        writer.writerow(dict(case=row['case'],reference_1_ns=row['reference_mean_ns'][0],
            candidate_2_ns=row['candidate_mean_ns'][0],candidate_3_ns=row['candidate_mean_ns'][1],
            reference_4_ns=row['reference_mean_ns'][1],latency_ratio=row['latency_ratio'],
            **row['pair_latency_ratios'],reference_1_bytes=row['reference_allocated_bytes'][0],
            candidate_2_bytes=row['candidate_allocated_bytes'][0],candidate_3_bytes=row['candidate_allocated_bytes'][1],
            reference_4_bytes=row['reference_allocated_bytes'][1]))
print(json.dumps(dict(report=str(P),csv=str(csv_path),case_count=len(rows),
    consistently_over_110_percent=result['consistently_over_110_percent'],
    consistently_under_90_percent_count=len(result['consistently_under_90_percent']),
    indexed_files=len(raw_files),indexed_bytes=sum(row['bytes'] for row in raw_files))))
