#!/usr/bin/env python3
"""Write independent tuning configurations without building or running workloads."""
import argparse
import copy
import json
from pathlib import Path


def prepare(config, destination):
    destination.mkdir(parents=True, exist_ok=False)
    boundary_sizes = sorted({threshold + offset for threshold in (131072, 262144, 524288, 1048576) for offset in (-1, 0, 1)})
    outputs = []
    for factor, values, reference in (("wal-chunk-bytes", (4096, 8192, 16384, 32768, 65536), 4096),
                                       ("async-offload-threshold", (131072, 262144, 524288, 1048576), 1048576)):
        for value in values:
            candidate = copy.deepcopy(config)
            # Tuning uses the same product and frozen driver in every role.
            candidate["after"] = candidate["before"]
            if "before_commit" in candidate:
                candidate["after_commit"] = candidate["before_commit"]
            baseline = {"wal-chunk-bytes": 4096, "async-offload-threshold": 1048576}
            candidate["before_settings"] = baseline
            candidate["after_settings"] = baseline | {factor: value}
            candidate["workload_args"] = ["--payload-bytes", ",".join(map(str, boundary_sizes)), "--concurrency", "1,4,16"]
            label = f"{factor}-{value}"
            if candidate.get("environment") != "macos":
                candidate["prefix"] = "slimfaas-pr506-" + ("wal" if factor == "wal-chunk-bytes" else "offload") + str(value)
            path = destination / (label + ".json")
            path.write_text(json.dumps(candidate, indent=2) + "\n")
            outputs.append({"config": path.name, "factor": factor, "reference": reference, "candidate": value,
                            "blocks": "http", "output_must_be_fresh": True})
    (destination / "index.json").write_text(json.dumps({"schema_version": 1, "campaigns": outputs,
        "durability": "Keep the same minimum persisted copies and all other runtime settings; no product defaults are changed.",
        "execution": "Manual only: run-platform-matrix.py --config CONFIG --output FRESH_DIRECTORY --blocks http"}, indent=2) + "\n")
    return outputs


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    prepare(json.loads(args.config.read_text()), args.output)
