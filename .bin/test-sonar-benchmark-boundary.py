#!/usr/bin/env python3
"""Protect runtime/SDK analysis while excluding benchmark and chaos programs."""

import importlib.util
from pathlib import Path
import unittest
import tempfile
import xml.etree.ElementTree as ET


spec = importlib.util.spec_from_file_location("boundary", Path(__file__).with_name("check-sonar-benchmark-boundary.py"))
boundary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(boundary)


class AnalysisBoundaryTests(unittest.TestCase):
    def test_recursive_patterns_include_direct_and_nested_diagnostic_sources(self):
        exclusions = boundary.patterns()
        for path in ("benchmarks/SlimFaas.Benchmarks/Program.cs",
                     "benchmarks/SlimFaas.Benchmarks/Application/BenchmarkRunner.cs",
                     "tools/SlimFaas.MemoryLab/Program.cs",
                     "docs/reliability/2026-11-16/analyze-micro-abba-189.py",
                     ".bin/run-local-disk-chaos.sh", ".bin/slimdata_fault_agent.py"):
            with self.subTest(path=path):
                self.assertTrue(boundary.excluded(path, exclusions))

    def test_runtime_sdk_release_tools_and_evidence_remain_eligible(self):
        exclusions = boundary.patterns()
        for path in (*boundary.SENTINELS, ".bin/install-local-demo.sh", ".bin/package-local-demo.py",
                     ".bin/test-python-client.py", "docs/reliability/2026-11-16/endurance-summary-189.json",
                     "tests/SlimData.Tests/QueueElementExtensionsTests.cs"):
            with self.subTest(path=path):
                self.assertFalse(boundary.excluded(path, exclusions))

    def test_broad_inventory_cannot_exclude_runtime_or_official_sdk(self):
        for path in boundary.SENTINELS:
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "production/SDK"):
                boundary.inventory([path], ["**/*"])

    def test_completed_scan_must_contain_production_and_exclude_benchmark_sources(self):
        boundary.check_indexed(boundary.SENTINELS, boundary.patterns())
        with self.assertRaisesRegex(ValueError, "missing production files"):
            boundary.check_indexed(boundary.SENTINELS[1:], boundary.patterns())
        with self.assertRaisesRegex(ValueError, "excluded files"):
            boundary.check_indexed([*boundary.SENTINELS, ".bin/run-platform-matrix.py"], boundary.patterns())

    def test_mocking_dependency_cannot_hide_production_in_test_classification(self):
        with self.assertRaisesRegex(ValueError, "classified as test"):
            boundary.check_indexed(boundary.SENTINELS, boundary.patterns(), boundary.SENTINELS[1:])

    def test_submitted_metadata_requires_every_product_and_rejects_test_classification(self):
        projects = ["src/SlimFaas/SlimFaas.csproj", "src/SlimData/SlimData.csproj",
                    "src/SlimFaasMcp/SlimFaasMcp.csproj", "src/SlimFaasKafka/SlimFaasKafka.csproj",
                    "client/dotnet/SlimFaasClient/src/SlimFaasClient/SlimFaasClient.csproj"]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for index, project in enumerate(projects):
                directory = root / str(index)
                directory.mkdir()
                document = ET.Element("ProjectInfo", xmlns="http://www.sonarsource.com/msbuild/integration/2015/1")
                for name, value in {"FullPath": str(boundary.ROOT / project),
                                    "ProjectType": "Product", "IsExcluded": "false"}.items():
                    ET.SubElement(document, name).text = value
                ET.ElementTree(document).write(directory / "ProjectInfo.xml")
            self.assertEqual(5, len(boundary.check_projects(root)))
            broken = root / "0/ProjectInfo.xml"
            broken.write_text(broken.read_text().replace("Product", "Test"))
            with self.assertRaisesRegex(ValueError, "classified as test"):
                boundary.check_projects(root)
            broken.unlink()
            with self.assertRaisesRegex(ValueError, "Missing submitted"):
                boundary.check_projects(root)


if __name__ == "__main__":
    unittest.main(verbosity=2)
