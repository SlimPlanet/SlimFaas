#!/usr/bin/env python3
"""Check cancelled Raft upload memory in two isolated Linux cgroups."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument('--publication', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--engine', choices=('docker', 'podman'), default='docker')
    parser.add_argument('--image', default='mcr.microsoft.com/dotnet/runtime-deps:10.0')
    options = parser.parse_args()
    publication = options.publication.resolve()
    if not (publication / 'SlimData.Tests').is_file():
        parser.error('Publish the socket-memory diagnostic before running this control')
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    prefix = 'slimdata-socket-' + uuid.uuid4().hex[:12]
    server, client = prefix + '-peer', prefix + '-sender'
    mount = str(publication) + ':/probe:ro,z'
    engine = options.engine
    try:
        subprocess.run([engine, 'run', '-d', '--name', server, '--memory', '256m',
                        '--volume', mount, '--entrypoint', '/probe/SlimData.Tests',
                        options.image, 'server'], check=True)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            logs = subprocess.check_output([engine, 'logs', server], text=True)
            if 'LISTENING_WITHOUT_ACCEPT' in logs:
                break
            time.sleep(.1)
        else:
            raise TimeoutError('The isolated receiver did not start')
        command = [engine, 'run', '--name', client, '--memory', '512m', '--network',
                   'container:' + server, '--volume', mount, '--entrypoint',
                   '/probe/SlimData.Tests', options.image, 'client']
        with (output / 'client.log').open('w') as log:
            code = subprocess.call(command, stdout=log, stderr=subprocess.STDOUT)
        inspection = json.loads(subprocess.check_output([engine, 'inspect', client, server], text=True))
        (output / 'containers.json').write_text(json.dumps(inspection, indent=2) + '\n')
        sender = next(row for row in inspection if row['Name'].lstrip('/') == client)
        passed = code == 0 and not sender['State'].get('OOMKilled', False)
        files = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in publication.iterdir() if p.is_file() and p.name in ('SlimData.Tests', 'SlimData.Tests.dll', 'SlimData.dll')}
        (output / 'result.json').write_text(json.dumps({'passed': passed, 'exit_code': code,
            'publication_sha256': files, 'image': options.image, 'command': command,
            'kernel_socket_budget_bytes': 64 * 1024**2, 'finished_utc': time.time()}, indent=2) + '\n')
        print((output / 'client.log').read_text())
        return 0 if passed else 1
    finally:
        # No runtime volumes are involved. Retain both stopped diagnostic
        # containers and their complete inspection alongside the output.
        subprocess.run([engine, 'stop', '--time', '1', server], check=False)


if __name__ == '__main__':
    raise SystemExit(main())
