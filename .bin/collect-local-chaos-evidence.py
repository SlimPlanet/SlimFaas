#!/usr/bin/env python3
"""Export compact, versioned evidence without altering a campaign's state."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def rows(path):
    with path.open(encoding="utf-8") as source:
        for line in source:
            if line.strip():
                yield json.loads(line)


def resource_ranges(root):
    outcomes, resources = Counter(), {}
    ledger = root / "client.jsonl"
    if ledger.is_file():
        for record in rows(ledger):
            outcomes[record["event"] + ":" + record.get("kind", "")] += 1
            if record["event"] == "resource-sample":
                for key, value in record["metrics"].items():
                    entry = resources.setdefault(key, {"minimum": value, "maximum": value})
                    entry["minimum"] = min(entry["minimum"], value)
                    entry["maximum"] = max(entry["maximum"], value)
    return dict(outcomes), resources


def delivery_counts(root):
    deliveries, receipts = [], Counter()
    for path in sorted((root / "receipts").glob("*.jsonl")):
        for record in rows(path):
            receipts[record["event"]] += 1
            if record["event"] == "completed" and 200 <= record["status"] < 300:
                deliveries.append((record["id"], record.get("function", "")))
    return {"receipt_events": dict(receipts), "successful_deliveries": len(deliveries),
            "distinct_successful_ids": len({identifier for identifier, _ in deliveries}),
            "distinct_successful_consumer_pairs": len(set(deliveries)),
            "extra_successful_deliveries": len(deliveries) - len(set(deliveries))}


def artifact_output(path, artifact_root, source_roots=()):
    output = path.resolve()
    if not output.is_relative_to(artifact_root.resolve()):
        raise ValueError("Evidence output must stay inside the repository artifacts directory")
    if output.suffix != ".json" or output.exists():
        raise ValueError("Evidence output must be a new JSON file")
    if any(output.is_relative_to(root.resolve()) for root in source_roots):
        raise ValueError("Evidence output cannot alter an input campaign")
    return output


def collect(root):
    reports = {path.name: json.loads(path.read_text(encoding="utf-8"))
               for path in sorted(root.glob("report*.json"))}
    parameters = {path.name: json.loads(path.read_text(encoding="utf-8"))
                  for path in sorted(root.glob("parameters*.json"))}
    outcomes, resources = resource_ranges(root)
    # Content hashes identify original raw evidence retained outside Git.
    files = {}
    for path in sorted(root.iterdir()):
        if path.is_file():
            files[path.name] = {"bytes": path.stat().st_size, "sha256": digest(path)}
    return {"run": root.name, "reports": reports, "parameters": parameters,
            "ledger_events": outcomes, **delivery_counts(root),
            "resource_ranges_across_nodes_and_phases": resources, "files": files}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("runs", type=Path, nargs="+")
    options = parser.parse_args()
    try:
        output = artifact_output(options.output, Path(__file__).resolve().parents[1] / "artifacts", options.runs)
    except ValueError as error:
        parser.error(str(error))
    result = {"schema": 1, "runs": [collect(root) for root in options.runs]}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as destination:
        destination.write(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
