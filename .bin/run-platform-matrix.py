#!/usr/bin/env python3
"""Run serial, retained ABBA comparisons on macOS, Podman or the dedicated kind cluster."""

import argparse
import csv
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import time


ROOT = Path(__file__).resolve().parents[1]
ORDER = ("before", "after", "after", "before")


class WorkloadFailure(ValueError):
    pass


def write(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def invoke(command, log, check=False, **kwargs):
    with log.open("a") as stream:
        stream.write(json.dumps([str(item) for item in command]) + "\n")
        stream.flush()
        return subprocess.run([str(item) for item in command], stdout=stream,
                              stderr=subprocess.STDOUT, check=check, **kwargs).returncode


def memory_measurement(directory):
    status = json.loads((directory / "status.json").read_text())
    document = json.loads((directory / "operations.json").read_text())
    receipts = json.loads((directory / "operations.receipts.json").read_text())
    report = document["report"]
    if document.get("validationFailures") is None:
        raise ValueError("Incomplete workload, receipts or all-node validation")
    if not status["passed"] or not receipts["passed"] or report["Failed"] or document["validationFailures"]:
        raise WorkloadFailure("Workload or reconciliation failed")
    all_operations = next(row for row in report["Operations"] if row["Operation"] == "All")
    with (directory / "memory.csv").open() as stream:
        rss = [int(row["rss_kb"]) / 1024 for row in csv.DictReader(stream) if row["phase"] == "load"]
    result = {"rate": report["Rate"], "p99_ms": all_operations["P99Milliseconds"], "rss_mib": max(rss)}
    if not all(math.isfinite(value) and value > 0 for value in result.values()):
        raise ValueError("Missing or invalid measured resource/latency evidence")
    return result


def compare_memory(baseline, candidate):
    try:
        left = memory_measurement(baseline)
    except (OSError, ValueError, KeyError, StopIteration) as error:
        return {"verdict": "INCONCLUSIVE", "reason": "Baseline: " + repr(error)}
    try:
        right = memory_measurement(candidate)
    except WorkloadFailure as error:
        return {"verdict": "FAIL", "reason": "Candidate: " + repr(error), "checks": {"integrity": False}}
    except (OSError, ValueError, KeyError, StopIteration) as error:
        return {"verdict": "INCONCLUSIVE", "reason": "Candidate: " + repr(error)}
    ratios = {key: right[key] / left[key] for key in left}
    checks = {"throughput": ratios["rate"] >= .90, "p99": ratios["p99_ms"] <= 1.20,
              "rss": ratios["rss_mib"] <= 1.15}
    return {"verdict": "PASS" if all(checks.values()) else "FAIL", "checks": checks,
            "baseline": left, "candidate": right, "ratios": ratios}


def fingerprint(comparison):
    if "checks" in comparison:
        return comparison["checks"]
    result = {}
    for category in ("Sync", "Async", "AsyncWorkloads"):
        for row in comparison.get(category) or []:
            key = (category, row.get("PayloadBytes"), row.get("Concurrency"), row.get("LoadShape"))
            result[str(key)] = row.get("GuardrailsPassed", row.get("Verdict") == "PASS")
    resources = comparison.get("AsyncCriteria") or {}
    result["resources"] = resources.get("ResourceGuardrailsPassed")
    result["scaling"] = (comparison.get("Scaling") or {}).get("Verdict")
    return result


def contradictory(pairs):
    return any(fingerprint(pairs[f"a1-b{candidate}"]) != fingerprint(pairs[f"a4-b{candidate}"])
               for candidate in (2, 3))


def export_vm(relative, output):
    archive = output / "raw.tar.gz"
    with archive.open("wb") as stream:
        subprocess.run(["podman", "machine", "ssh", "tar", "-C",
                        "/var/home/core/slimfaas-pr506/" + relative,
                        "-czf", "-", "workload"], stdout=stream, check=True)
    with tarfile.open(archive) as source:
        source.extractall(output, filter="data")
    return output / "workload"


def run_one(config, suite, scenario, concurrency, variant, ordinal, output):
    if shutil.disk_usage(ROOT).free < 20 * 1024**3:
        raise RuntimeError("Mac free disk below 20 GiB; reclaim inactive caches outside measurements")
    vm_free = None
    if config["environment"] != "macos":
        disk = subprocess.check_output(["podman", "machine", "ssh", "df", "-B1", "--output=avail", "/"], text=True)
        vm_free = int(disk.splitlines()[-1])
        if vm_free < 20 * 1024**3:
            raise RuntimeError("Podman VM free disk below 20 GiB; retain state and reclaim inactive images")
    output.mkdir()
    write(output / "phase.json", {"start": time.time(), "variant": variant,
          "mac_free_bytes": shutil.disk_usage(ROOT).free, "vm_free_bytes": vm_free})
    common = ["--suite", suite, "--variant", variant, "--ordinal", str(ordinal)]
    if suite == "memory":
        common += ["--scenario", scenario, "--memory-concurrency", str(concurrency)]
    log = output / "launcher.log"
    if config["environment"] == "macos":
        command = [sys.executable, ROOT / ".bin/run-platform-workload.py", "--runtime", config[variant],
                   "--benchmark", config["benchmark"], "--memorylab", config["memorylab"],
                   "--output", output / "workload"] + common
        code = invoke(command, log)
        raw = output / "workload"
    elif config["environment"] == "podman":
        name = config["prefix"] + "-" + output.parent.name + "-" + output.name
        relative = "podman/" + name
        vm_directory = "/var/home/core/slimfaas-pr506/" + relative
        invoke(["podman", "machine", "ssh", "mkdir", "-p", vm_directory], log, check=True)
        invoke(["podman", "machine", "ssh", "chmod", "0777", vm_directory], log, check=True)
        command = ["podman", "run", "--name", name, "--rm", "--user", "1000:1000", "--memory", "8g",
                   "--cpus", "6", "--volume", vm_directory + ":/evidence", "--entrypoint", "python3",
                   config[variant], "/src/.bin/run-platform-workload.py", "--runtime", "/app/SlimFaas",
                   "--benchmark", "/tools/benchmark/SlimFaasBenchmark", "--memorylab", "/tools/memorylab/SlimFaas.MemoryLab",
                   "--output", "/evidence/workload"] + common
        code = invoke(command, log)
        raw = export_vm(relative, output)
    else:
        name = config["prefix"] + "-" + output.parent.name + "-" + output.name
        base = [sys.executable, ROOT / ".bin/run-kubernetes-campaign.py"]
        arguments = ["--kubeconfig", config["kubeconfig"], "--namespace", name, "--output", output]
        command = base + ["launch"] + arguments + ["--image", config[variant], "--controller-image",
            config["controller_image"], "--source-commit", config[variant + "_commit"],
            "--suite", suite, "--probes", "normal", "--variant", variant, "--ordinal", str(ordinal)]
        if suite == "memory":
            command += ["--scenario", scenario, "--memory-concurrency", str(concurrency)]
        code = invoke(command, log)
        if code == 0:
            code = invoke(base + ["wait"] + arguments + ["--timeout", "7200"], log)
        invoke(base + ["collect"] + arguments, log)
        invoke(base + ["stop"] + arguments, log)
        raw = export_vm("runs/" + name, output)
    write(output / "execution.json", {"exit_code": code, "raw": str(raw), "finished": time.time()})
    return raw


def compare_http(config, baseline, candidate, output):
    command = config["compare_command"] + ["compare", "--criteria", "non-regression", "--baseline",
        str(baseline / "results.json"), "--candidate", str(candidate / "results.json"), "--output", str(output)]
    output.mkdir(exist_ok=True)
    code = invoke(command, output / "compare.log")
    if not (output / "comparison.json").exists():
        return {"verdict": "INCONCLUSIVE", "reason": "Comparer failed or a required report is missing", "exit_code": code}
    result = json.loads((output / "comparison.json").read_text())
    result["verdict"] = "INCONCLUSIVE" if result.get("InconclusiveReason") else "PASS" if result["Passed"] else "FAIL"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--blocks", default="http,set-c12,set-c48,mixed-c12,mixed-c48")
    options = parser.parse_args()
    config = json.loads(options.config.read_text())
    if config["environment"] not in ("macos", "podman", "kubernetes"):
        parser.error("Unknown environment")
    root = options.output.resolve()
    root.mkdir(parents=True, exist_ok=options.resume)
    write(root / "config.json", config)
    index = root / "matrix.json"
    results = json.loads(index.read_text()) if index.exists() else {"started": time.time(), "blocks": {}}
    for block in options.blocks.split(","):
        if block in results["blocks"]:
            continue
        suite = "http" if block == "http" else "memory"
        scenario = "slimdata-mixed" if block.startswith("mixed") else "slimdata-set"
        concurrency = int(block.split("-c")[-1]) if suite == "memory" else None
        attempts = []
        for attempt in (1, 2):
            directory = root / f"{block}-attempt-{attempt}"
            directory.mkdir(exist_ok=options.resume)
            raw = {}
            for ordinal, variant in enumerate(ORDER, 1):
                output = directory / f"{ordinal}-{variant}"
                if (output / "execution.json").exists():
                    raw[ordinal] = Path(json.loads((output / "execution.json").read_text())["raw"])
                else:
                    raw[ordinal] = run_one(config, suite, scenario, concurrency, variant, ordinal, output)
                print(json.dumps({"block": block, "attempt": attempt, "ordinal": ordinal, "variant": variant}), flush=True)
            pairs = {}
            for baseline in (1, 4):
                for candidate in (2, 3):
                    key = f"a{baseline}-b{candidate}"
                    pairs[key] = compare_http(config, raw[baseline], raw[candidate], directory / key) if suite == "http" else compare_memory(raw[baseline], raw[candidate])
            conflict = contradictory(pairs)
            result = {"pairs": pairs, "contradictory_brackets": conflict,
                      "verdict": "INCONCLUSIVE" if conflict or any(row["verdict"] == "INCONCLUSIVE" for row in pairs.values())
                      else "PASS" if all(row["verdict"] == "PASS" for row in pairs.values()) else "FAIL"}
            write(directory / "comparison.json", result)
            attempts.append(result)
            if not conflict:
                break
        results["blocks"][block] = {"attempts": attempts, "verdict": attempts[-1]["verdict"]}
        write(index, results)
    results["finished"] = time.time()
    results["verdict"] = "INCONCLUSIVE" if any(row["verdict"] == "INCONCLUSIVE" for row in results["blocks"].values()) else "PASS" if all(row["verdict"] == "PASS" for row in results["blocks"].values()) else "FAIL"
    write(index, results)
    return 0 if results["verdict"] == "PASS" else 2 if results["verdict"] == "INCONCLUSIVE" else 1


if __name__ == "__main__":
    raise SystemExit(main())
