import argparse
import concurrent.futures
import gzip
import json
import os
from pathlib import Path
import subprocess
import threading
import time
from urllib.request import build_opener, ProxyHandler

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--resume', action='store_true')
parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/slimdata-lts/paired-native')
parser.add_argument('--candidate-runtime', type=Path, default=ROOT / 'artifacts/slimdata-lts/lts-default-retry-aot/SlimFaas.exe')
options = parser.parse_args()
OUTPUT = options.output.resolve()
OUTPUT.mkdir(exist_ok=options.resume)
http = build_opener(ProxyHandler({}))

def msys(path):
    path = path.resolve()
    return '/' + path.drive[0].lower() + path.as_posix()[2:]

def sample(port):
    try:
        with http.open(f'http://127.0.0.1:{port}/metrics', timeout=1) as response:
            text = response.read().decode()
        return {'port': port, 'metrics': '\n'.join(line for line in text.splitlines()
                if line.startswith(('slimdata_', 'system_runtime_')))}
    except OSError as error:
        return {'port': port, 'error': str(error)}

def monitor(stop, destination):
    with gzip.open(destination, 'wt', encoding='utf-8') as output, concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        while not stop.is_set():
            started = time.monotonic()
            output.write(json.dumps({'utc': time.time(), 'nodes': list(pool.map(sample, (31021,31022,31023)))}) + '\n')
            output.flush()
            stop.wait(max(0, 1 - (time.monotonic() - started)))

statuses = json.loads((OUTPUT / 'status.json').read_text()) if options.resume else []
for ordinal, variant in enumerate(('baseline', 'candidate', 'candidate', 'baseline'), 1):
    if any(record['ordinal'] == ordinal for record in statuses):
        continue
    run = OUTPUT / f'{ordinal:02}-{variant}'
    runtime = ROOT / 'artifacts/slimdata-lts/baseline-aot/SlimFaas.exe' if variant == 'baseline' else options.candidate_runtime
    env = os.environ | {'DOTNET_ROOT': str(ROOT / 'artifacts/dotnet-10.0.403'),
        'BENCHMARK_PROFILE': 'quick', 'BENCHMARK_SKIP_BUILD': 'true',
        'BENCHMARK_RUNTIME': msys(runtime), 'BENCHMARK_RUN_ROOT': msys(run),
        'BENCHMARK_DURATION_SECONDS': '5', 'BENCHMARK_WARMUP_SECONDS': '2',
        'BENCHMARK_REPETITIONS': '1', 'BENCHMARK_CONCURRENCY': '1,4',
        'BENCHMARK_PAYLOAD_BYTES': '65536,262144,1048576,2000000,2097152,4194304',
        'BENCHMARK_SCALE_MESSAGES': '200', 'BENCHMARK_SCALE_CONCURRENCY': '32'}
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env['PATH']
    stop = threading.Event()
    thread = threading.Thread(target=monitor, args=(stop, OUTPUT / f'{ordinal:02}-{variant}-metrics.jsonl.gz'))
    thread.start()
    started = time.time()
    try:
        with (OUTPUT / f'{ordinal:02}-{variant}.log').open('w', encoding='utf-8') as log:
            code = subprocess.call(['C:/Program Files/Git/bin/bash.exe', '.bin/slimfaas-local-benchmark.sh'],
                cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
    finally:
        stop.set()
        thread.join()
    statuses.append({'variant': variant, 'ordinal': ordinal, 'started_utc': started,
                     'finished_utc': time.time(), 'exit_code': code})
    (OUTPUT / 'status.json').write_text(json.dumps(statuses, indent=2))
    print(json.dumps(statuses[-1]), flush=True)
    time.sleep(5)
raise SystemExit(int(any(record['exit_code'] for record in statuses)))

