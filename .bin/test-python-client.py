#!/usr/bin/env python3
"""Run the Python SDK tests and export real executed lines for Sonar coverage."""

import argparse
import dis
from pathlib import Path
import sys
import trace
import types
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT / 'client/python/slimfaas-client'


def executable_lines(code):
    lines = {line for _, line in dis.findlinestarts(code) if line is not None and line > 0}
    for constant in code.co_consts:
        if isinstance(constant, types.CodeType):
            lines.update(executable_lines(constant))
    return lines


def write_coverage(destination, sources, counts, root=ROOT):
    hits = {(Path(filename).resolve(), line) for (filename, line), count in counts.items() if count > 0}
    coverage = ET.Element('coverage', version='1')
    for source in sorted(sources):
        source = source.resolve()
        file = ET.SubElement(coverage, 'file', path=source.relative_to(root).as_posix())
        code = compile(source.read_text(encoding='utf-8'), str(source), 'exec')
        for line in sorted(executable_lines(code)):
            ET.SubElement(file, 'lineToCover', lineNumber=str(line),
                          covered=str((source, line) in hits).lower())
    destination.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(coverage).write(destination, encoding='utf-8', xml_declaration=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--coverage', required=True, type=Path)
    args, pytest_args = parser.parse_known_args()
    sys.path.insert(0, str(CLIENT / 'src'))
    import pytest

    tracer = trace.Trace(count=True, trace=False)
    result = tracer.runfunc(pytest.main, [str(CLIENT / 'tests'), *pytest_args])
    write_coverage(args.coverage, (CLIENT / 'src/slimfaas_client').glob('*.py'), tracer.results().counts)
    return int(result)


if __name__ == '__main__':
    raise SystemExit(main())
