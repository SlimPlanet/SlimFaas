"""Cold replay a copy of the original CI image; submit only diagnostic GET requests."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import zipfile

if os.geteuid() != 0 or any(os.readlink('/proc/self/ns/' + name) == os.readlink('/proc/1/ns/' + name)
                          for name in ('net', 'mnt')):
    raise RuntimeError('Requires isolated network and mount namespaces')
subprocess.run(['ip', 'link', 'set', 'lo', 'up'], check=True)

def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        while block := stream.read(1024 * 1024):
            value.update(block)
    return value.hexdigest()

root = Path('/root/.cache/slimdata-lts/traffic-comparison-7e1')
archive_path = root / 'ci-7920-linux.zip'
destination = root / 'artifacts/file-availability-replay-4e80'
destination.mkdir(exist_ok=False)
with zipfile.ZipFile(archive_path) as archive:
    for entry in archive.infolist():
        if not entry.filename.startswith('disk/') and entry.filename != 'disk-resources/disk.img':
            continue
        target = (destination / entry.filename).resolve()
        if not target.is_relative_to(destination) or (entry.external_attr >> 16) & 0o170000 == 0o120000:
            raise ValueError(entry.filename)
        archive.extract(entry, destination)
image = destination / 'disk-resources/disk.img'
before = digest(image)
assert before == 'e1db74fd3954da1f5e23ad98cd39ef3b33db93a3979e8d1363dd29bf05cb632d'
mount = destination / 'disk-resources/mount'
mount.mkdir(exist_ok=True)
run = destination / 'disk'
manifest = run / 'manifest.yaml'
original = manifest.read_text()
(run / 'manifest-before-relocation.yaml').write_text(original)
old_root = '/home/runner/work/SlimFaas/SlimFaas'
updated = original.replace(old_root + '/artifacts/local-chaos/disk-resources/mount', str(mount))
updated = updated.replace(old_root + '/artifacts/local-chaos/disk', str(run)).replace(old_root, str(root))
updated = updated.replace('/opt/hostedtoolcache/Python/3.11.18/x64/bin/python3', sys.executable)
assert updated != original
manifest.write_text(updated)
spec = importlib.util.spec_from_file_location('chaos', root / '.bin/test-slimdata-local-chaos.py')
chaos = importlib.util.module_from_spec(spec)
spec.loader.exec_module(chaos)
options = chaos.parse_arguments(['--resume', '--runtime', str(root / 'runtime-leader-event-4e80/SlimFaas.dll'),
    '--dotnet', '/root/.cache/slimdata-lts/dotnet/dotnet', '--source-commit',
    '4e80c9a19f3f5e2a633f23ad00b510e8b25676dc', '--output', str(run),
    '--state-directory', str(mount / 'state'), '--port-base', '35120', '--recovery-timeout', '300'])
evidence = {'schema': 1, 'source_archive_sha256': hashlib.sha256(archive_path.read_bytes()).hexdigest(),
    'image_sha256_before': before, 'source_commit': options.source_commit,
    'runtime_sha256': hashlib.sha256(options.runtime.read_bytes()).hexdigest(),
    'note': 'Fresh copy of original failed CI image; only external manifest paths relocated. Diagnostic submits GETs only; normal runtime recovery can mutate the copied state. Old report remains failed; receipts are not reclassified by this focused diagnostic.',
    'file_id': '6b5f6e010dae483ab3a7004754038374', 'observations': []}
subprocess.run(['mount', '-o', 'loop', str(image), str(mount)], check=True)
campaign = None
try:
    assert (mount / '.slimdata-chaos-volume').read_text().strip() == 'slimdata-local-chaos-v1'
    campaign = chaos.Campaign(options)
    started = time.monotonic()
    campaign.start()
    def file_available():
        copies = []
        for port in campaign.ports:
            status, _, body = chaos.request(port, '/data/files/' + evidence['file_id'], timeout=5)
            actual = hashlib.sha256(body).hexdigest()
            copies.append({'port': port, 'status': status, 'bytes': len(body), 'sha256': actual})
            if status == 200 and actual != 'a42a4e2a4a7706730878d794488b3b011c6110afbcbaf7da2b11d87467e7384b':
                raise AssertionError('Original confirmed file returned different bytes')
            if status not in (200, 404):
                raise AssertionError(f'Original file unavailable after readiness: HTTP {status}')
        evidence['observations'].append({'utc': time.time(), 'copies': copies})
        return all(copy['status'] == 200 for copy in copies)
    chaos.wait_until('original confirmed file on every node', file_available, 300)
    evidence['original_file_verified_seconds'] = time.monotonic() - started
    campaign.verify()
    evidence['confirmed_immutable_state_verified'] = True
    campaign.capture('original-file-replay')
except BaseException as error:
    evidence['error'] = repr(error)
    raise
finally:
    if campaign is not None:
        campaign.stop()
    subprocess.run(['umount', str(mount)], check=True)
    evidence['image_sha256_after'] = digest(image)
    (destination / 'observations.json').write_text(json.dumps(evidence, indent=2))
