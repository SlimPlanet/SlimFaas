#!/usr/bin/env python3
"""Run serial, retained ABBA comparisons on macOS, Podman or the dedicated kind cluster."""

import argparse
import csv
from functools import wraps
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import time


ROOT = Path(__file__).resolve().parents[1]
ORDER = ("before", "after", "after", "before")


class WorkloadFailure(ValueError):
    pass


def isolated_measurement(function):
    @wraps(function)
    def execute(*args, **kwargs):
        artifacts = ROOT / "artifacts"
        artifacts.mkdir(exist_ok=True)
        marker = artifacts / "platform-measurement.json"
        with (artifacts / "platform-measurement.lock").open("a+") as lock:
            try:
                if os.name == "nt":
                    import msvcrt
                    if lock.seek(0, os.SEEK_END) == 0:
                        lock.write("\0")
                        lock.flush()
                    lock.seek(0)
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as error:
                raise RuntimeError("Another platform measurement owns this host") from error
            write(marker, {"pid": os.getpid(), "started": time.time(), "output": str(args[-1])})
            try:
                return function(*args, **kwargs)
            finally:
                marker.unlink(missing_ok=True)
    return execute


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
    parameters = json.loads((directory / "parameters.json").read_text())
    backend = parameters["backend"]
    node_count = 3 if backend == "Local" else len(backend["nodes"])
    expected_nodes = {f"slimfaas-{index}" for index in range(node_count)}
    rounds = {}
    with (directory / "memory.csv").open() as stream:
        for row in csv.DictReader(stream):
            if row["phase"] != "load":
                continue
            sample = rounds.setdefault(row["timestamp"], {})
            rss = float(row["rss_kb"]) / 1024
            if row["node"] in sample or not math.isfinite(rss) or rss <= 0:
                raise ValueError("Duplicate node or invalid RSS in a load sample")
            sample[row["node"]] = rss
    if not rounds or not expected_nodes or any(set(sample) != expected_nodes for sample in rounds.values()):
        raise ValueError("Missing all-node RSS evidence during load")
    # Sum each sampling round first: neither the largest node nor the sum of
    # independent per-node maxima is the sampled peak cluster working set.
    peak_rss = max(sum(sample.values()) for sample in rounds.values())
    resources = document.get("resources")
    if document.get("schemaVersion", 1) >= 2:
        if not resources or not resources.get("Samples") or not resources.get("InitialSnapshot") or not resources.get("FinalProcessSnapshot"):
            raise ValueError("Missing shared collector boundary/sample evidence")
        peak_rss = resources["PeakWorkingSetBytes"] / 1024**2
        cpu = resources.get("CpuMillisecondsPerMessage")
        if cpu is None or not math.isfinite(cpu) or cpu < 0:
            raise ValueError("Invalid measured CPU evidence")
    result = {"rate": report["Rate"], "p99_ms": all_operations["P99Milliseconds"], "rss_mib": peak_rss}
    if not all(math.isfinite(value) and value > 0 for value in result.values()):
        raise ValueError("Missing or invalid measured resource/latency evidence")
    if resources:
        result["cpu_ms_per_operation"] = resources["CpuMillisecondsPerMessage"]
        result["cpu_seconds"] = resources["CpuSeconds"]
    return result


def compare_memory(baseline, candidate):
    # Inspect candidate integrity even when the reference lacks resource evidence.
    right = left = None
    errors = []
    for label, directory in (("Candidate", candidate), ("Baseline", baseline)):
        try:
            measured = memory_measurement(directory)
            if label == "Candidate":
                right = measured
            else:
                left = measured
        except WorkloadFailure as error:
            if label == "Candidate":
                return {"verdict": "FAIL", "reason": repr(error), "checks": {"integrity": False}}
            errors.append(label + ": " + repr(error))
        except (OSError, ValueError, KeyError, StopIteration) as error:
            errors.append(label + ": " + repr(error))
    if errors:
        return {"verdict": "INCONCLUSIVE", "reason": "; ".join(errors)}
    ratios = {key: right[key] / left[key] for key in ("rate", "p99_ms", "rss_mib")}
    checks = {"throughput": ratios["rate"] >= .90, "p99": ratios["p99_ms"] <= 1.20,
              "rss": ratios["rss_mib"] <= 1.15}
    return {"verdict": "PASS" if all(checks.values()) else "FAIL", "checks": checks,
            "baseline": left, "candidate": right, "ratios": ratios}


def validate_frozen_drivers(baseline, candidate):
    try:
        left = json.loads((baseline / "parameters.json").read_text())
        right = json.loads((candidate / "parameters.json").read_text())
        keys = ("driver_sha256", "driver_files", "memorylab_sha256", "memorylab_files", "workload_sha256",
                "local_harness_sha256", "sdk_fixture_sha256", "receipt_fixture_sha256")
        for key in keys:
            a, b = left.get(key), right.get(key)
            if isinstance(a, dict) and isinstance(b, dict):
                a = {Path(path).name: digest for path, digest in a.items()}
                b = {Path(path).name: digest for path, digest in b.items()}
            if a is None or b is None or a != b:
                return "Missing or different frozen driver evidence: " + key
    except (OSError, ValueError) as error:
        return "Cannot verify frozen drivers: " + str(error)
    return None


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
        for member in source:
            parts = Path(member.name).parts
            # These complete originals remain in the verified archive and on
            # the VM. Comparers need only reports, not a second WAL/CSV copy.
            if len(parts) > 1 and parts[1] in ("state", "latency-samples.csv", "latency-samples.csv.gz"):
                continue
            source.extract(member, output, filter="data")
    with archive.open("rb") as stream:
        archive_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    write(output / "raw-archive.json", {"archive": archive.name, "sha256": archive_hash,
        "vm_original": "/var/home/core/slimfaas-pr506/" + relative,
        "large_members_retained_in_archive": ["workload/state/", "workload/latency-samples.csv.gz"]})
    return output / "workload"


@isolated_measurement
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
    if config.get(variant + "_commit"):
        common += ["--source-commit", config[variant + "_commit"]]
    common += config.get("workload_args", [])
    settings = config.get(variant + "_settings", {})
    for key in ("async-offload-threshold", "wal-chunk-bytes"):
        if key in settings:
            common += ["--" + key, str(settings[key])]
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
        source = ROOT / ".bin/run-platform-workload.py"
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        vm_sources = "/var/home/core/slimfaas-pr506/images/sources/" + source_hash
        vm_source = vm_sources + "/run-platform-workload.py"
        exists = subprocess.run(["podman", "machine", "ssh", "test", "-f", vm_source]).returncode == 0
        if not exists:
            invoke(["podman", "machine", "ssh", "mkdir", "-p", vm_sources], log, check=True)
            with source.open("rb") as stream:
                invoke(["podman", "machine", "ssh", "cat > " + vm_source], log, check=True, stdin=stream)
            invoke(["podman", "machine", "ssh", "chmod", "0444", vm_source], log, check=True)
        write(output / "workload-source.json", {"sha256": source_hash, "vm_path": vm_source})
        command = ["podman", "run", "--name", name, "--rm", "--user", "1000:1000", "--memory", "8g",
                   "--cpus", "6", "--volume", vm_directory + ":/evidence:Z",
                   "--volume", vm_source + ":/src/.bin/run-platform-workload.py:ro,Z", "--entrypoint", "python3",
                   config[variant], "/src/.bin/run-platform-workload.py", "--runtime", "/app/SlimFaas",
                   "--benchmark", "/tools/benchmark/SlimFaas.Benchmarks", "--memorylab", "/tools/memorylab/SlimFaas.MemoryLab",
                   "--output", "/evidence/workload"] + common
        code = invoke(command, log)
        raw = export_vm(relative, output)
    else:
        name = config["prefix"] + "-" + output.parent.name + "-" + output.name
        base = [sys.executable, ROOT / ".bin/run-kubernetes-campaign.py"]
        # The phase already contains its metadata/log. The launcher requires
        # a fresh directory so a previous controller cannot be overwritten.
        arguments = ["--kubeconfig", config["kubeconfig"], "--namespace", name, "--output", output / "controller"]
        command = base + ["launch"] + arguments + ["--image", config[variant], "--controller-image",
            config["controller_image"], "--source-commit", config[variant + "_commit"],
            "--suite", suite, "--probes", "normal", "--variant", variant, "--ordinal", str(ordinal)]
        if suite == "memory":
            command += ["--scenario", scenario, "--memory-concurrency", str(concurrency)]
        command += config.get("workload_args", [])
        for key in ("async-offload-threshold", "wal-chunk-bytes"):
            if key in settings:
                command += ["--" + key, str(settings[key])]
        code = invoke(command, log)
        if code == 0:
            code = invoke(base + ["wait"] + arguments + ["--timeout", str(config.get("phase_timeout_seconds", 604800))], log)
        invoke(base + ["collect"] + arguments, log)
        invoke(base + ["stop"] + arguments, log, check=True)
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
    result["verdict"] = "FAIL" if not result.get("CandidateHasNoFailures", True) else "INCONCLUSIVE" if result.get("InconclusiveReason") else "PASS" if result["Passed"] else "FAIL"
    return result


def compare_features(config, baseline, candidate, output):
    output.mkdir(exist_ok=True)
    observed = {}
    for label, directory in (("candidate", candidate), ("baseline", baseline)):
        command = config["compare_command"] + ["report", "--campaign", str(directory), "--output", str(output / (label + ".md"))]
        code = invoke(command, output / (label + ".log"))
        report = output / (label + ".json")
        if code or not report.exists():
            observed[label] = {"Verdict": "INCONCLUSIVE", "reason": "Offline feature report missing"}
        else:
            observed[label] = json.loads(report.read_text())
    if observed["candidate"]["Verdict"] == "FAIL":
        return {"verdict": "FAIL", "checks": {"integrity": False}, "reports": observed}
    complete = all(report["Verdict"] == "OBSERVED" for report in observed.values())
    return {"verdict": "OBSERVED" if complete else "INCONCLUSIVE", "checks": {"complete": complete},
            "reason": "Descriptive feature measurements; no established performance budget yet", "reports": observed}


def freeze_configuration(root, config, blocks, resume):
    """Refuse mixed sources/settings before changing any existing campaign file."""
    identity = {"schema_version": 1, "config": config, "blocks": blocks,
                "controller_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                "workload_sha256": hashlib.sha256((ROOT / ".bin/run-platform-workload.py").read_bytes()).hexdigest()}
    if config["environment"] == "macos":
        identity["binary_sha256"] = {}
        for key in ("before", "after", "benchmark", "memorylab"):
            path = Path(config[key])
            with path.open("rb") as stream:
                identity["binary_sha256"][key] = hashlib.file_digest(stream, "sha256").hexdigest()
            for dependency in sorted(path.parent.glob("*.dll")):
                with dependency.open("rb") as stream:
                    identity["binary_sha256"][key + ":" + dependency.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    else:
        for key in ("before", "after", "controller_image"):
            if key == "controller_image" and config["environment"] != "kubernetes":
                continue
            if not re.fullmatch(r"(?:.+@)?sha256:[0-9a-f]{64}", config.get(key, "")):
                raise ValueError("Frozen campaigns require an immutable image digest for " + key)
    frozen = root / "campaign-identity.json"
    if resume:
        if not frozen.exists() or json.loads(frozen.read_text()) != identity:
            raise ValueError("Resume refused: campaign sources, binaries, blocks or configuration differ (or identity is missing). Use a fresh output directory.")
    else:
        write(frozen, identity)
        write(root / "config.json", config)


def combine_verdicts(rows, conflict=False):
    # Missing metrics never erase a known failure.
    verdicts = [row["verdict"] for row in rows]
    if "FAIL" in verdicts:
        return "FAIL"
    return "INCONCLUSIVE" if conflict or not verdicts or "INCONCLUSIVE" in verdicts else "OBSERVED" if "OBSERVED" in verdicts else "PASS"


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--blocks", default="http,set-c12,set-c48,mixed-c12,mixed-c48,data,features")
    options = parser.parse_args()
    config = json.loads(options.config.read_text())
    if config["environment"] not in ("macos", "podman", "kubernetes"):
        parser.error("Unknown environment")
    blocks = options.blocks.split(",")
    if set(blocks) - {"http", "set-c12", "set-c48", "mixed-c12", "mixed-c48", "data", "features"}:
        parser.error("Unknown measurement block")
    if config["environment"] != "macos" and not re.fullmatch(r"slimfaas-pr506-[a-z0-9-]{1,20}", config["prefix"]):
        parser.error("Use a dedicated short slimfaas-pr506-* campaign prefix")
    root = options.output.resolve()
    root.mkdir(parents=True, exist_ok=options.resume)
    freeze_configuration(root, config, blocks, options.resume)
    index = root / "matrix.json"
    results = json.loads(index.read_text()) if index.exists() else {"started": time.time(), "blocks": {}}
    for block in blocks:
        if block in results["blocks"]:
            continue
        suite = block if block in ("http", "data", "features") else "memory"
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
                    pairs[key] = compare_http(config, raw[baseline], raw[candidate], directory / key) if suite == "http" else compare_features(config, raw[baseline], raw[candidate], directory / key) if suite in ("data", "features") else compare_memory(raw[baseline], raw[candidate])
                    gap = validate_frozen_drivers(raw[baseline], raw[candidate])
                    if gap:
                        pairs[key]["provenance_gap"] = gap
                        if pairs[key]["verdict"] != "FAIL":
                            pairs[key]["verdict"] = "INCONCLUSIVE"
            conflict = contradictory(pairs)
            result = {"pairs": pairs, "contradictory_brackets": conflict,
                      "verdict": combine_verdicts(pairs.values(), conflict)}
            write(directory / "comparison.json", result)
            attempts.append(result)
            if not conflict:
                break
        results["blocks"][block] = {"attempts": attempts, "verdict": combine_verdicts(attempts)}
        write(index, results)
    results["finished"] = time.time()
    results["verdict"] = combine_verdicts(results["blocks"].values())
    write(index, results)
    return 0 if results["verdict"] in ("PASS", "OBSERVED") else 2 if results["verdict"] == "INCONCLUSIVE" else 1


if __name__ == "__main__":
    raise SystemExit(main())
