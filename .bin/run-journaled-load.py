#!/usr/bin/env python3
"""Reuse the platform resource sampler around one canonical C# load command."""

import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import threading
from types import SimpleNamespace


def drain_confirmed_async(workload, command, output, config, phase):
    if Path(command[0]).name != "SlimFaas.MemoryLab":
        return
    document = output / "operations.json"
    if not document.is_file():
        return # A failed driver retains its nonzero exit code and raw request intents.
    samples = json.loads(document.read_text()).get("samples", [])
    if not any(row.get("operation") == "Async" and row.get("Success") for row in samples):
        return
    phase["value"] = "drain"
    # Poll only independent receipts for already accepted IDs. Never resubmit.
    workload.verify_receipts(document, Path(config["receipts_root"]), timeout=180, allow_duplicates=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--backend-config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    command = options.command[1:] if options.command[:1] == ["--"] else options.command
    if not command or Path(command[0]).name not in ("SlimFaas.MemoryLab", "SlimFaas.Benchmarks"):
        parser.error("A canonical C# benchmark executable is required after --")
    spec = importlib.util.spec_from_file_location("workload", Path(__file__).with_name("run-platform-workload.py"))
    workload = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(workload)
    config = json.loads(options.backend_config.read_text())
    nodes = [node["http"] for node in config["nodes"]]
    options.output.mkdir(parents=True, exist_ok=False)
    sample_options = SimpleNamespace(output=options.output,
        node_controls=[node["control"] for node in config["nodes"]] if config.get("process_socket_statistics") else [])
    workload.snapshot(sample_options, nodes, config, "before")
    stop = threading.Event()
    phase = {"value": "load"}
    sampler = threading.Thread(target=workload.monitor, args=(sample_options, nodes, stop, phase))
    sampler.start()
    try:
        result = subprocess.run(command, check=False)
        drain_confirmed_async(workload, command, options.output, config, phase)
    finally:
        stop.set()
        sampler.join(10)
    if sampler.is_alive():
        raise TimeoutError("The bounded platform sampler did not stop")
    workload.snapshot(sample_options, nodes, config, "after")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
