"""Check original CI observations against confirmed/unknown ledger bounds, without replaying mutations."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('archives', type=Path, nargs='+')
options = parser.parse_args()
assert not options.output.exists()
results = []
for path in options.archives:
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            if not name.endswith('/client.jsonl'):
                continue
            prefix = name.removesuffix('client.jsonl')
            accepted, unknown, previous, last, violations = {}, set(), None, {}, []
            observations = 0
            with archive.open(name) as stream:
                for line_number, line in enumerate(stream, 1):
                    row = json.loads(line)
                    if row.get('kind') == 'counter':
                        if row['event'] == 'accepted':
                            accepted[row['id']] = int(row['response'])
                            unknown.discard(row['id'])
                        elif row['event'] == 'unknown' and row['id'] not in accepted:
                            unknown.add(row['id'])
                    if row['event'] != 'counter-observation':
                        continue
                    observations += 1
                    values = row['values']
                    floor = max(len(accepted), max(accepted.values(), default=0))
                    ceiling = len(accepted) + len(unknown)
                    if (len(set(accepted.values())) != len(accepted)
                            or any(not floor <= value <= ceiling for value in values)
                            or previous is not None and any(now < old for now, old in zip(values, previous))):
                        violations.append({'line': line_number, 'phase': row['phase'], 'values': values,
                                           'minimum': floor, 'possible': ceiling})
                    last[row['phase']] = values
                    previous = values
            report = json.loads(archive.read(prefix + 'report.json'))
            parameters = json.loads(archive.read(prefix + 'parameters.json'))
            unconverged = {phase: values for phase, values in last.items() if len(set(values)) != 1}
            if report['passed'] and unconverged:
                violations.append({'unconverged_final_observations': unconverged})
            results.append({'archive': str(path), 'archive_sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                'run': prefix.rstrip('/'), 'runtime_source_commit': parameters['commit'],
                'runtime_sha256': parameters['runtime_sha256'], 'original_report_passed': report['passed'],
                'observations': observations, 'confirmed_counter_responses': len(accepted),
                'highest_confirmed_counter_response': max(accepted.values(), default=0),
                'unknown_increments': len(unknown), 'final_values_by_phase': last,
                'violations': violations, 'audit_passed': observations > 0 and not violations})
document = {'schema': 1, 'audit_script_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    'note': 'Post-hoc read-only checks of original ledgers; no runtime mutations or rewritten campaign outcomes.',
    'results': results}
with options.output.open('x') as output:
    json.dump(document, output, indent=2)
raise SystemExit(int(not results or any(not row['audit_passed'] for row in results)))
