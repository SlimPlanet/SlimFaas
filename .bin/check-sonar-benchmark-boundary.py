#!/usr/bin/env python3
"""Verify the benchmark exclusion inventory and the completed SonarCloud scan."""

import argparse
import fnmatch
import json
import os
from pathlib import Path
import re
import subprocess
import time
from urllib.parse import urlencode
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
INVENTORY = ROOT / "eng/sonar-benchmark-exclusions.txt"
PROJECT = "SlimPlanet_SlimFaas"
EXCLUDED_PROJECTS = (
    "benchmarks/SlimFaas.Benchmarks/SlimFaas.Benchmarks.csproj",
    "benchmarks/SlimFaas.CollectionProbe/SlimFaas.CollectionProbe.csproj",
    "tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj",
    "docs/reliability/2026-11-12/socket-memory-probe/SocketMemoryProbe.csproj",
)
SENTINELS = (
    "src/SlimFaas/SendClient.cs",
    "src/SlimData/QueueElementExtensions.cs",
    "src/SlimFaasMcp/Program.cs",
    "src/SlimFaasKafka/Program.cs",
    "client/dotnet/SlimFaasClient/src/SlimFaasClient/SlimFaasClient.cs",
    "client/python/slimfaas-client/src/slimfaas_client/_client.py",
)


def patterns():
    return [line.strip() for line in INVENTORY.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith("#")]


def excluded(path, exclusions):
    # Sonar's **/ also matches zero directory components; fnmatch does not.
    return any(fnmatch.fnmatchcase(path, pattern) or
               ("**/" in pattern and excluded(path, [pattern.replace("**/", "", 1)]))
               for pattern in exclusions)


def inventory(files, exclusions):
    protected = [path for path in files if path.startswith(("src/", "client/")) or
                 (path.startswith("tools/") and not path.startswith("tools/SlimFaas.MemoryLab/"))]
    violations = [path for path in protected if excluded(path, exclusions)]
    if violations:
        raise ValueError("Benchmark exclusions cover production/SDK files: " + repr(violations))
    return {pattern: [path for path in files if excluded(path, [pattern])] for pattern in exclusions}


def check_indexed(paths, exclusions, main_paths=None, expected_paths=SENTINELS):
    violations = sorted(path for path in paths if excluded(path, exclusions))
    missing = sorted(set(expected_paths) - set(paths))
    if violations or missing:
        raise ValueError(f"Invalid analyzed scope: excluded files={violations}; missing production files={missing}")
    if main_paths is not None and set(expected_paths) - set(main_paths):
        raise ValueError("Production/SDK sources are incorrectly classified as test code")


def check_projects(scan_directory):
    expected = {"src/SlimFaas/SlimFaas.csproj", "src/SlimData/SlimData.csproj",
                "src/SlimFaasMcp/SlimFaasMcp.csproj", "src/SlimFaasKafka/SlimFaasKafka.csproj",
                "client/dotnet/SlimFaasClient/src/SlimFaasClient/SlimFaasClient.csproj"}
    observed = {}
    for path in scan_directory.rglob("ProjectInfo.xml"):
        document = ET.parse(path).getroot()
        fields = {element.tag.rsplit("}", 1)[-1]: element.text for element in document}
        full_path = fields.get("FullPath")
        if not full_path:
            raise ValueError("Scanner project metadata has no FullPath: " + str(path))
        try:
            relative = Path(full_path).resolve().relative_to(ROOT).as_posix()
        except ValueError:
            continue  # Scanner-generated external temporary projects are not repository products.
        product = fields.get("ProjectType") == "Product" or fields.get("IsTest") == "false"
        observed[relative] = {key: fields.get(key) for key in ("ProjectType", "IsTest", "IsExcluded")}
        if relative in expected and (not product or fields.get("IsExcluded") != "false"):
            raise ValueError("Production project is excluded or classified as test: " + relative)
        if relative in EXCLUDED_PROJECTS and fields.get("IsExcluded") != "true":
            raise ValueError("Benchmark project was not excluded: " + relative)
    if expected - observed.keys():
        raise ValueError("Missing submitted production project metadata: " + repr(sorted(expected - observed.keys())))
    return observed


def api(resource, parameters):
    headers = {}
    if os.environ.get("SONAR_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["SONAR_TOKEN"]
    request = Request("https://sonarcloud.io/api/" + resource + "?" + urlencode(parameters), headers=headers)
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def verify_scan(scan_directory):
    projects = check_projects(scan_directory)
    task_files = list(scan_directory.rglob("report-task.txt"))
    if len(task_files) != 1:
        raise ValueError("Expected exactly one completed scanner report-task.txt")
    task = dict(line.split("=", 1) for line in task_files[0].read_text().splitlines() if "=" in line)
    task_id = task.get("ceTaskId", "")
    if not re.fullmatch(r"[A-Za-z0-9_-]+", task_id):
        raise ValueError("Invalid compute-engine task identity")
    deadline = time.monotonic() + 600
    while True:
        result = api("ce/task", {"id": task_id})["task"]
        if result["status"] == "SUCCESS":
            break
        if result["status"] in ("FAILED", "CANCELED") or time.monotonic() >= deadline:
            raise RuntimeError("Sonar compute-engine task did not complete successfully: " + result["status"])
        time.sleep(5)
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    scope = {"pullRequest": str(event["pull_request"]["number"])} if "pull_request" in event else {
        "branch": os.environ["GITHUB_REF_NAME"]}
    paths = []
    main_paths = []
    page = 1
    while True:
        document = api("components/tree", {"component": PROJECT, "qualifiers": "FIL,UTS",
                                           "ps": 500, "p": page, **scope})
        paths.extend(item["path"] for item in document["components"])
        main_paths.extend(item["path"] for item in document["components"] if item["qualifier"] == "FIL")
        if page * 500 >= document["paging"]["total"]:
            break
        page += 1
    # PR APIs omit unchanged files. Submitted project metadata above verifies
    # the full C# scope; the server verifies its reported files after processing.
    expected = set(SENTINELS) & set(paths)
    check_indexed(paths, patterns(), main_paths, expected_paths=expected)
    misclassified = [path for path in set(paths) - set(main_paths) if path.endswith(".cs") and
                     (path.startswith("src/") or path.startswith("client/dotnet/SlimFaasClient/src/"))]
    if misclassified:
        raise ValueError("Production code classified as tests: " + repr(misclassified))
    coverage = {}
    for path in (SENTINELS[1], SENTINELS[4], SENTINELS[5]):
        if path not in paths:
            coverage[path] = {"scope": "Unchanged file omitted by the PR API; project submission checked separately"}
            continue
        document = api("measures/component", {"component": PROJECT + ":" + path,
            "metricKeys": "lines_to_cover,uncovered_lines,coverage", **scope})
        measures = {item["metric"]: item.get("value") for item in document["component"]["measures"]}
        if int(measures.get("lines_to_cover") or 0) <= int(measures.get("uncovered_lines") or 0):
            raise ValueError("No imported covered lines for production sentinel " + path)
        coverage[path] = measures
    raw_log = (ROOT / "artifacts/sonar-boundary/scanner-end.log").read_bytes()
    # Windows PowerShell's Tee-Object writes UTF-16; pwsh writes UTF-8.
    log = raw_log.decode("utf-16" if raw_log.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig")
    if not re.search(r"Coverage Report Statistics:.*\b[1-9]\d* main files with coverage", log):
        raise ValueError("Scanner did not confirm imported C# production coverage")
    if "python-client-coverage.xml" not in log or "Imported coverage data for" not in log:
        raise ValueError("Scanner did not confirm the Python SDK coverage report")
    return {"task": result, "scope": scope, "submitted_projects": projects, "analyzed_files": sorted(paths),
            "main_files": sorted(main_paths), "coverage": coverage}


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--print-patterns", action="store_true")
    parser.add_argument("--scan-directory", type=Path)
    parser.add_argument("--report", type=Path)
    options = parser.parse_args()
    exclusions = patterns()
    if options.print_patterns:
        print(",".join(exclusions))
        return
    files = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().strip("\0").split("\0")
    # Include the manifest's newly added verifier during an uncommitted local validation.
    files = sorted(set(files) | {str(Path(__file__).resolve().relative_to(ROOT))})
    report = {"inventory": inventory(files, exclusions), "excluded_projects": list(EXCLUDED_PROJECTS)}
    for project in EXCLUDED_PROJECTS:
        if ET.parse(ROOT / project).getroot().findtext(".//SonarQubeExclude") != "true":
            raise ValueError("Missing explicit SonarQubeExclude: " + project)
    if options.scan_directory:
        report["scan"] = verify_scan(options.scan_directory)
    report["passed"] = True
    if options.report:
        options.report.parent.mkdir(parents=True, exist_ok=True)
        options.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": True, "excluded_files": len({p for paths in report["inventory"].values() for p in paths}),
                      "completed_scan_checked": "scan" in report}))


if __name__ == "__main__":
    main()
