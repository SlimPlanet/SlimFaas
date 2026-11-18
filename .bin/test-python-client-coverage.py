#!/usr/bin/env python3
"""Check that the Python SDK coverage adapter preserves uncovered source lines."""

import importlib.util
from pathlib import Path
import tempfile
import trace
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('sdk_tests', Path(__file__).with_name('test-python-client.py'))
sdk_tests = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sdk_tests)


class CoverageTests(unittest.TestCase):
    def test_report_includes_unexecuted_branches_and_nested_functions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            source = root / 'source & module.py'
            source.write_text('def work(flag):\n    if flag:\n        return 1\n    return 2\n\n'
                              'def unused():\n    return 3\n', encoding='utf-8')
            namespace = {'__file__': str(source)}
            tracer = trace.Trace(count=True, trace=False)
            tracer.runctx(compile(source.read_text(), str(source), 'exec'), namespace, namespace)
            tracer.runfunc(namespace['work'], True)
            output = root / 'reports/coverage.xml'
            sdk_tests.write_coverage(output, [source], tracer.results().counts, root)
            report = ET.parse(output).getroot()
            self.assertEqual(report.attrib, {'version': '1'})
            file = report.find('file')
            self.assertEqual(file.attrib['path'], source.name)
            lines = {int(line.attrib['lineNumber']): line.attrib['covered'] for line in file}
            self.assertEqual(lines[3], 'true')
            self.assertEqual(lines[4], 'false')
            self.assertEqual(lines[7], 'false')
            self.assertNotIn(5, lines)


if __name__ == '__main__':
    unittest.main(verbosity=2)
