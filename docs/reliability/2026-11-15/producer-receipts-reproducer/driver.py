"""Bounded diagnostic: reuse the existing confirmed/unknown ledger and fsynced receipts."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import time
import traceback

sys.path.insert(0, '/src/.bin')
spec = importlib.util.spec_from_file_location('local', '/src/.bin/test-slimdata-local-chaos.py')
local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(local)
from slimdata_kubernetes_backend import create_campaign

parser = argparse.ArgumentParser()
parser.add_argument('--chunk-size', type=int, required=True)
parser.add_argument('--ready-only', action='store_true')
extra, remaining = parser.parse_known_args()
options = local.parse_arguments(remaining)
campaign = create_campaign(options, vars(local))
config = json.loads(options.backend_config.read_text())
output = campaign.output
result = {'started_utc': time.time(), 'source': options.source_commit,
          'chunk_size_marker_requested': extra.chunk_size, 'ready_only': extra.ready_only,
          'workload_started': False, 'passed': False,
          'mode': 'Linux ARM64 managed/trimmed .NET 11, bounded diagnostic, not formal performance acceptance',
          'messages_requested': 0 if extra.ready_only else 200, 'payload_bytes': 1048576, 'concurrency': 16,
          'post_send_observation_seconds': 180, 'request_timeout_seconds': 15,
          'fixture_note': 'Existing chaos function/client journals fsync every record; this instrumentation differs from the aggregate-only HTTP target.'}
for name, path in [('runtime', campaign.runtime), ('slimfaas_dll', campaign.runtime.parent / 'SlimFaas.dll'),
                   ('slimdata_dll', campaign.runtime.parent / 'SlimData.dll'), ('harness', Path(local.__file__)),
                   ('driver', Path(__file__))]:
    with path.open('rb') as stream:
        result[name + '_sha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
expected = next(v for v in json.loads((Path(__file__).parent / 'variants.json').read_text()).values() if v['source'] == options.source_commit)
for key, digest in expected['hashes'].items():
    assert result[key + '_sha256'] == digest, (key, result[key + '_sha256'], digest)
(output / 'parameters.json').write_text(json.dumps(result | {'backend': config}, indent=2) + '\n')


sample_sequence = 0
runtime_samples = []


def sample(deadline):
    global sample_sequence
    if time.monotonic() + 24 >= deadline:
        return
    sequence = sample_sequence
    sample_sequence += 1
    captured = {'utc': time.time(), 'nodes': [],
                'guest_diskstats': Path('/proc/diskstats').read_text(),
                'guest_io_pressure': Path('/proc/pressure/io').read_text()}
    failures = []
    for index, port in enumerate(campaign.ports):
        node = {'node': index, 'url': local.endpoint(port)}
        try:
            status, headers, body = local.request(port, '/metrics', timeout=1, redirects=False)
            (output / f'sample-{sequence:03}-node-{index}.prom').write_bytes(body)
            node.update({'metrics_status': status, 'metrics_location': headers.get('Location')})
            if status != 200:
                failures.append(f'node {index}: metrics HTTP {status}')
            leader_status, headers, address = local.request(campaign.raft + index, '/SlimData/leader', timeout=1, redirects=False)
            node.update({'leader_status': leader_status, 'leader_address': address[:1000].decode(errors='replace'), 'leader_location': headers.get('Location')})
        except local.NETWORK_ERRORS as error:
            node['error'] = repr(error)
            failures.append(f'node {index}: {error!r}')
        captured['nodes'].append(node)
    pods = campaign.api('GET', campaign.resource('pods'))['items']
    captured['runtime_pods'] = [{'name': pod['metadata']['name'], 'uid': pod['metadata']['uid'],
                                'node': pod['spec'].get('nodeName'),
                                'containers': pod.get('status', {}).get('containerStatuses', [])}
                               for pod in pods if pod['metadata'].get('labels', {}).get('app') == 'slimfaas']
    runtime_samples.append(captured['runtime_pods'])
    campaign.client.append({'event': 'diagnostic-sample', **captured})
    if failures:
        raise RuntimeError('; '.join(failures))


try:
    campaign.start()
    result['ready_utc'] = time.time()
    markers = []
    for node in range(3):
        path = campaign.node_state_directory(node) / 'wal/slimdata-chunk-size'
        value = path.read_text() if path.exists() else None
        assert value == (str(extra.chunk_size) if extra.chunk_size else None)
        markers.append({'node': node, 'path': str(path), 'value': value})
    result['markers'] = markers
    campaign.capture('initial')
    if not extra.ready_only:
        campaign.phase = 'bounded-200-messages-1mib-c16'
        result['workload_started'] = True
        started = time.monotonic()
        def send(_):
            import uuid
            identifier = uuid.uuid4().hex
            return campaign.mutate('async', '/async-function/chaos/consume',
                                   local.payload(1048576, identifier), identifier=identifier)
        with ThreadPoolExecutor(max_workers=16) as pool:
            responses = list(pool.map(send, range(200)))
        result['send_seconds'] = time.monotonic() - started
        result['accepted'] = len(campaign.accepted)
        result['unknown_count'] = len(campaign.unknown)
        result['unexpected_http_statuses'] = [status for status, _, _, _ in responses if status != 202]
        result['accepted_per_second'] = len(campaign.accepted) / result['send_seconds']
        from observer import observe_window
        cutoff = time.time() + 180
        observation_started = time.monotonic()
        errors = observe_window(180, lambda: local.read_receipts(campaign.receipts),
                                campaign.receipt_succeeded, sample)
        result['observation_errors'] = errors
        result['observed_after_send_seconds'] = time.monotonic() - observation_started
        rows = [row for row in local.read_receipts(campaign.receipts) if row['utc'] <= cutoff]
        valid = []
        for row in rows:
            try:
                if campaign.receipt_succeeded(row):
                    valid.append(row)
            except Exception as error:
                errors['integrity'].append({'id': row.get('id'), 'error': repr(error)})
        counts = {}
        for row in valid:
            counts[row['id']] = counts.get(row['id'], 0) + 1
        result['missing_confirmed'] = sorted(set(campaign.accepted) - counts.keys())
        result['bounded_repeated_deliveries'] = {key: count for key, count in counts.items() if count > 1}
        result['accounting'] = campaign.audit()
        result['max_observed_container_restarts'] = max((container.get('restartCount', 0)
            for frame in runtime_samples for pod in frame for container in pod['containers']), default=0)
        result['passed'] = (not result['missing_confirmed']
                            and not result['bounded_repeated_deliveries']
                            and not result['unexpected_http_statuses']
                            and not any(errors.values())
                            and result['max_observed_container_restarts'] == 0)
    else:
        result['passed'] = True
    campaign.capture('final')
except Exception:
    result['passed'] = False
    result['error'] = traceback.format_exc()
    (output / 'error.txt').write_text(result['error'])
finally:
    result['finished_utc'] = time.time()
    (output / 'report.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({k: v for k, v in result.items() if k not in ['markers', 'accounting', 'error']}), flush=True)
    if result.get('error'):
        print(result['error'], flush=True)
raise SystemExit(0 if result['passed'] else 1)
