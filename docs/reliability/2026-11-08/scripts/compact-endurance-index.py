"""Summarize unknown outcomes while preserving reports and original archive attribution."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--index', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    options = parser.parse_args()
    document = json.loads(options.index.read_text(encoding='utf-8'))
    document['summary_note'] = (
        'Reports and parameters are unchanged. Accounting unknown-ID arrays are summarized '
        'by operation/outcome; complete IDs remain in the original SHA-256-attributed ZIPs. '
        'Each summarized accounting member has its own raw-byte SHA-256 and size.')
    for artifact in document['artifacts']:
        path = options.index.parent / (artifact['name'] + '.zip')
        with zipfile.ZipFile(path) as archive:
            for name, record in artifact['records'].items():
                if not name.endswith('/accounting.json'):
                    continue
                raw = archive.read(name)
                assert json.loads(raw) == record, (path, name)
                unknown = record.pop('unknown')
                counts = Counter((row['kind'], row['outcome']) for row in unknown)
                record['unknown_summary'] = {
                    'count': len(unknown),
                    'by_kind_and_outcome': [
                        {'kind': kind, 'outcome': outcome, 'count': count}
                        for (kind, outcome), count in sorted(counts.items())]}
                record['original_member'] = {
                    'path': name, 'bytes': len(raw),
                    'sha256': hashlib.sha256(raw).hexdigest()}
    with options.output.open('x', encoding='utf-8') as output:
        json.dump(document, output, indent=2)
        output.write('\n')


if __name__ == '__main__':
    main()
