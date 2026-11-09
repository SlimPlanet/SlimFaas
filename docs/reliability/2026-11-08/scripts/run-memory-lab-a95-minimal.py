import concurrent.futures
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import threading
import time
from urllib.request import build_opener, ProxyHandler

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'artifacts/slimdata-lts/paired-memory-lab-a95'
OUTPUT.mkdir(exist_ok=False)
spec = importlib.util.spec_from_file_location('chaos', ROOT / '.bin/test-slimdata-local-chaos.py')
chaos = importlib.util.module_from_spec(spec)
spec.loader.exec_module(chaos)
dotnet = ROOT / 'artifacts/dotnet-10.0.403/dotnet.exe'
os.environ['DOTNET_ROOT'] = str(dotnet.parent)
os.environ['PATH'] = str(dotnet.parent) + os.pathsep + os.environ['PATH']
lab = ROOT / 'tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll'
http = build_opener(ProxyHandler({}))

def sample(port):
    try:
        with http.open(f'http://127.0.0.1:{port}/metrics', timeout=1) as response:
            text = response.read().decode()
        return {'port': port, 'metrics': '\n'.join(line for line in text.splitlines()
                if line.startswith(('slimdata_', 'system_runtime_')))}
    except OSError as error:
        return {'port': port, 'error': str(error)}

def monitor(stop, destination, phase):
    with gzip.open(destination, 'wt', encoding='utf-8') as output:
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
            while not stop.is_set():
                started = time.monotonic()
                output.write(json.dumps({'utc': time.time(), 'phase': phase['current'],
                    'nodes': list(pool.map(sample, (39121,39122,39123)))}) + '\n')
                output.flush()
                stop.wait(max(0, 1 - (time.monotonic() - started)))

statuses = []
for ordinal, variant in enumerate(('baseline', 'candidate', 'candidate', 'baseline'), 1):
    run = OUTPUT / f'{ordinal:02}-{variant}'
    runtime = ROOT / 'artifacts/slimdata-lts' / ('baseline-aot' if variant == 'baseline' else 'final-a95-aot-minimal') / 'SlimFaas.exe'
    options = chaos.parse_arguments(['--output', str(run), '--runtime', str(runtime), '--port-base', '39120',
                                     '--scenarios', 'api', '--recovery-timeout', '180'])
    campaign = chaos.Campaign(options)
    manifest = campaign.manifest.read_text()
    assert manifest.count('  chaos-wake:\n') == 1
    assert manifest.count("      SlimFaas/ReplicasMin: '0'") == 1
    campaign.manifest.write_text(manifest.replace('  chaos-wake:\n', '  memory-function:\n')
        .replace("      SlimFaas/ReplicasMin: '0'", "      SlimFaas/ReplicasMin: '1'"))
    metadata = {'variant': variant, 'ordinal': ordinal, 'runtime_sha256': hashlib.sha256(runtime.read_bytes()).hexdigest(),
        'driver_sha256': hashlib.sha256(lab.read_bytes()).hexdigest(), 'started_utc': time.time(),
        'manifest_sha256': hashlib.sha256(campaign.manifest.read_bytes()).hexdigest(), 'commands': [],
        'source_commit': '496e8739e5f7b4326f64520fe4caa4e0e23f8d06' if variant == 'baseline' else 'a95d49e8cd48c433e482a724e5f0c77c7ea29eb5',
        'harness_sha256': hashlib.sha256((ROOT / '.bin/test-slimdata-local-chaos.py').read_bytes()).hexdigest(),
        'snapshot_interval_entries': 50, 'snapshot_interval_bytes': 8388608}
    (run / 'parameters.json').write_text(json.dumps(metadata, indent=2))
    stop, phase = threading.Event(), {'current': 'starting'}
    thread = threading.Thread(target=monitor, args=(stop, run / 'metrics.jsonl.gz', phase))
    try:
        campaign.start()
        chaos.wait_until('MemoryLab function ready',
            lambda: chaos.request(campaign.entry, '/function/memory-function/health')[0] == 200, 180)
        thread.start()
        accepted_async = 0
        expected_digest = hashlib.sha256(bytes(ord('a') + i % 23 for i in range(4096))).hexdigest()
        for scenario in ('slimdata-set', 'slimdata-mixed'):
            for concurrency in (12, 48):
                label = f'{scenario}-c{concurrency}'
                for duration, stage in ((2, 'warmup'), (10, 'measure')):
                    phase['current'] = label + '-' + stage
                    prefix = f'{variant}-{ordinal}-{label}-{stage}'
                    command = [str(dotnet), str(lab), 'load', '--scenario', scenario,
                        '--duration', str(duration), '--concurrency', str(concurrency), '--first-port', '39121',
                        '--nodes', '3', '--payload-bytes', '4096', '--keys-per-worker', '16',
                        '--key-prefix', prefix, '--validate', 'true',
                        '--csv', str(run / f'{label}-{stage}.csv'), '--json', str(run / f'{label}-{stage}.json'),
                        '--phase-file', str(run / 'phase')]
                    metadata['commands'].append(command)
                    (run / 'parameters.json').write_text(json.dumps(metadata, indent=2))
                    with (run / f'{label}-{stage}.log').open('w', encoding='utf-8') as log:
                        code = subprocess.call(command, env=os.environ, stdout=log, stderr=subprocess.STDOUT)
                    statuses.append({'variant': variant, 'ordinal': ordinal, 'scenario': scenario,
                        'concurrency': concurrency, 'stage': stage, 'exit_code': code})
                    (OUTPUT / 'status.json').write_text(json.dumps(statuses, indent=2))
                    print(json.dumps(statuses[-1]), flush=True)
                    if code:
                        raise SystemExit(code)
                    report = json.loads((run / f'{label}-{stage}.json').read_text())['report']
                    accepted_async += sum(row['Completed'] for row in report['Operations'] if row['Operation'] == 'Async')
                    phase['current'] = label + '-' + stage + '-reconcile'
                    def delivered():
                        rows = [row for row in chaos.read_receipts(campaign.receipts)
                                if row['event'] == 'completed' and 200 <= row['status'] < 300]
                        if any(row['bytes'] != 4096 or row['sha256'] != expected_digest or not row['element_id'] for row in rows):
                            raise AssertionError('MemoryLab real function received a wrong body or missing element ID')
                        identifiers = {row['element_id'] for row in rows}
                        return len(identifiers) == accepted_async
                    chaos.wait_until('every accepted MemoryLab Async reached the real function', delivered, 180)
                    completed = [row for row in chaos.read_receipts(campaign.receipts) if row['event'] == 'completed']
                    (run / 'async-reconciliation.json').write_text(json.dumps({
                        'accepted': accepted_async, 'distinct_delivered_element_ids': len({row['element_id'] for row in completed}),
                        'completed_deliveries': len(completed), 'expected_sha256': expected_digest}, indent=2))
        campaign.capture('final')
    finally:
        stop.set()
        if thread.is_alive():
            thread.join()
        campaign.stop()
