#!/usr/bin/env python3
"""Exercise memory-report phase filtering through the built CLI."""

import csv
import io
from pathlib import Path
import subprocess
import tempfile
import unittest


DLL = (Path(__file__).resolve().parents[1] / "tools/SlimFaas.MemoryLab/bin/Release/net10.0"
       / "SlimFaas.MemoryLab.dll")


class MemoryReportTests(unittest.TestCase):
    def report(self, rows):
        self.assertTrue(DLL.is_file(), "Build tools/SlimFaas.MemoryLab in Release first")
        with tempfile.TemporaryDirectory() as output:
            path = Path(output) / "memory.csv"
            path.write_text("timestamp,node,pid,rss_kb,vsz_kb,phase\n" + rows)
            result = subprocess.run(["dotnet", str(DLL), "report", "--csv", str(path)],
                                    check=True, text=True, capture_output=True, timeout=15)
        return list(csv.DictReader(io.StringIO(result.stdout)))[0]

    def test_validation_memory_is_excluded_from_measured_peak(self):
        row = self.report(
            "2026-01-01T00:00:00Z,node-0,1,1024,1024,load\n"
            "2026-01-01T00:00:01Z,node-0,1,2048,2048,load\n"
            "2026-01-01T00:00:02Z,node-0,1,1048576,1048576,validation\n"
            "2026-01-01T00:00:03Z,node-0,1,3072,3072,cooldown\n")
        # The report uses the latter half of the load samples for its trend.
        self.assertEqual("1", row["load_samples"])
        self.assertEqual(2.0, float(row["peak_mb"]))
        self.assertEqual(3.0, float(row["cooldown_end_mb"]))

    def test_legacy_samples_without_phase_remain_load_samples(self):
        row = self.report(
            "2026-01-01T00:00:00Z,node-0,1,1024,1024\n"
            "2026-01-01T00:00:01Z,node-0,1,2048,2048\n")
        self.assertEqual("1", row["load_samples"])
        self.assertEqual(2.0, float(row["peak_mb"]))


if __name__ == "__main__":
    unittest.main()
