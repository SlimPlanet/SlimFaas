#!/usr/bin/env python3
"""Check job recovery across a temporary leader pause, without restarting nodes.

Requires Linux/macOS, Python 3, lsof and a built SlimFaas runtime. Runs an isolated
three-node cluster with temporary state and loopback ports. Usage from the repo:
    python3 tests/SlimFaas.Tests/Local/test_job_recovery.py [path/to/SlimFaas]
The default runtime is the Debug net10.0 DLL. The script also acts as its own job
workload; no external function, Kubernetes cluster or container engine is needed.
"""
import importlib.util
import os
from pathlib import Path
import re
import random
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[3]
if len(sys.argv) > 1 and sys.argv[1] == '--execute':
    root, marker = Path(sys.argv[2]), sys.argv[3]
    with (root / 'starts').open('a') as out:
        out.write(marker + '\n')
    deadline = time.monotonic() + 90
    while not (root / 'release').exists():
        if time.monotonic() > deadline:
            raise TimeoutError('Job release gate was not opened')
        time.sleep(.1)
    sys.exit(0)

if os.name == 'nt':
    raise SystemExit('This test requires POSIX SIGSTOP/SIGCONT and lsof.')

spec = importlib.util.spec_from_file_location('job_test', ROOT / 'tests/SlimFaas.Tests/Local/test_job_concurrency.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)

def ports():
    for _ in range(100):
        sockets = []
        try:
            first = socket.socket()
            first.bind(('127.0.0.1', random.randrange(20000, 28000)))
            base = first.getsockname()[1]
            sockets.append(first)
            for port in range(base + 1, base + 7):
                sock = socket.socket()
                sockets.append(sock)
                sock.bind(('127.0.0.1', port))
            return base
        except OSError:
            pass
        finally:
            for sock in sockets:
                sock.close()
    raise RuntimeError('No contiguous ports available')

def listener_pid(port):
    return int(subprocess.check_output(['lsof', '-t', '-iTCP:' + str(port), '-sTCP:LISTEN'], text=True).strip())

def leader(raft_ports, excluded=None):
    for port in raft_ports:
        if port == excluded:
            continue
        try:
            with urllib.request.urlopen(f'http://127.0.0.1:{port}/SlimData/leader', timeout=2) as response:
                match = re.search(r'Leader address is http://127\.0\.0\.1:(\d+)', response.read().decode())
                if match:
                    return int(match.group(1))
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
    return None

runtime = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'src/SlimFaas/bin/Debug/net10.0/SlimFaas.dll'
command = ['dotnet', str(runtime)] if runtime.suffix == '.dll' else [str(runtime)]
with tempfile.TemporaryDirectory(prefix='slimfaas-job-recovery-') as temporary:
    root = Path(temporary)
    base = ports()
    raft_ports = list(range(base + 4, base + 7))
    manifest = {
        'schemaVersion': 1, 'name': 'job-recovery',
        'cluster': {'nodes': 3, 'entrypointPort': base, 'nodeHttpPortBase': base + 1, 'raftPortBase': base + 4},
        'state': {'mode': 'persistent', 'directory': str(root / 'state')},
        'jobs': {'recovery': {
            'command': [sys.executable, str(Path(__file__).resolve()), '--execute', str(root)],
            'workingDirectory': str(root),
            'annotations': {'SlimFaas/Job': 'true', 'SlimFaas/DefaultVisibility': 'Public', 'SlimFaas/NumberParallelJob': '2'},
            'ttlSecondsAfterFinished': 3600, 'backoffLimit': 0, 'restartPolicy': 'Never'}}}
    manifest_path = root / 'local.yaml'
    manifest_path.write_text('\n'.join(helper.yaml_mapping(manifest)) + '\n')
    subprocess.run([*command, 'local', 'validate', '-f', str(manifest_path)], check=True)
    with (root / 'supervisor.log').open('w') as log:
        process = subprocess.Popen([*command, 'local', 'up', '-f', str(manifest_path)], stdout=log,
                                   stderr=subprocess.STDOUT, cwd=ROOT, start_new_session=True)
        paused = None
        try:
            for port in range(base, base + 4):
                helper.wait_for_ready(f'http://127.0.0.1:{port}', process)
            pids = {port: listener_pid(port) for port in raft_ports}
            old_leader = leader(raft_ports)
            assert old_leader in raft_ports, old_leader
            follower_port = next(port for port in raft_ports if port != old_leader) - 3
            api = f'http://127.0.0.1:{follower_port}'
            expected = {}
            for index in range(6):
                created = helper.request(api, '/job/recovery', {'Args': [f'before-{index}']})
                expected[created['Id']] = 'Succeeded'
            paused = pids[old_leader]
            assert os.getpgid(paused) == process.pid
            os.kill(paused, signal.SIGSTOP)
            print(f'Paused leader {old_leader}, PID {paused}; six accepted jobs.', flush=True)
            deadline = time.monotonic() + 45
            new_leader = None
            while time.monotonic() < deadline:
                new_leader = leader(raft_ports, excluded=old_leader)
                if new_leader and new_leader != old_leader:
                    break
                time.sleep(.25)
            assert new_leader and new_leader != old_leader, 'No replacement leader elected'
            print(f'Leader changed to {new_leader}; submitting six more jobs.', flush=True)
            api = f'http://127.0.0.1:{new_leader - 3}'
            for index in range(6):
                created = helper.request(api, '/job/recovery', {'Args': [f'during-{index}']})
                expected[created['Id']] = 'Succeeded'
            os.kill(paused, signal.SIGCONT)
            paused = None
            (root / 'release').touch()
            helper.wait_for_jobs(api, 'recovery', expected, process)
            helper.wait_for_ready(f'http://127.0.0.1:{old_leader - 3}', process)
            for index in range(6):
                created = helper.request(api, '/job/recovery', {'Args': [f'after-{index}']})
                expected[created['Id']] = 'Succeeded'
            helper.wait_for_jobs(api, 'recovery', expected, process)
            starts = (root / 'starts').read_text().splitlines()
            assert len(starts) == len(set(starts)) == 18, starts
            assert pids == {port: listener_pid(port) for port in raft_ports}, 'A node restarted'
            print('PASS: 18/18 accepted jobs succeeded once across three waves; leader changed; all three node PIDs unchanged.', flush=True)
        except BaseException:
            log.flush()
            print((root / 'supervisor.log').read_text(errors='replace')[-16000:])
            raise
        finally:
            if paused is not None:
                os.kill(paused, signal.SIGCONT)
            helper.stop(process)
