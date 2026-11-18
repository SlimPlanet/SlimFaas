#!/usr/bin/env python3
"""Validate the fixed two-round collection probe matrix and retain all samples."""
import argparse
import json
from pathlib import Path
import statistics


def summarize(root):
    runs = {}
    case_names = None
    for revision in ("baseline", "candidate"):
        for mode in ("jit", "aot"):
            for repetition in (1, 2):
                key = f"round-{repetition}-{revision}-{mode}"
                rows = json.loads((root / (key + ".json")).read_text())
                cases = {}
                for row in rows:
                    cases.setdefault(row["Case"], []).append(row)
                if case_names is not None and set(cases) != case_names:
                    raise ValueError(f"Case matrix differs: {key}")
                case_names = set(cases)
                for name, samples in cases.items():
                    if sorted(row["Sample"] for row in samples) != [0, 1, 2]:
                        raise ValueError(f"Missing/duplicate samples: {key}/{name}")
                    if len({row["Result"] for row in samples}) != 1:
                        raise ValueError(f"Inconsistent results: {key}/{name}")
                runs[key] = rows
    comparisons = []
    for mode in ("jit", "aot"):
        for case in sorted(case_names):
            values = {}
            observed = set()
            for revision in ("baseline", "candidate"):
                repetitions = []
                for repetition in (1, 2):
                    samples = [row for row in runs[f"round-{repetition}-{revision}-{mode}"] if row["Case"] == case]
                    observed.update(row["Result"] for row in samples)
                    repetitions.append({field: statistics.mean(row[field] for row in samples)
                                        for field in ("Nanoseconds", "Bytes")})
                values[revision] = {field: statistics.median(row[field] for row in repetitions)
                                    for field in ("Nanoseconds", "Bytes")}
                values[revision]["repetition_means"] = repetitions
            if len(observed) != 1:
                raise ValueError(f"Before/after behavior differs: {mode}/{case}")
            comparisons.append({"mode": mode, "case": case, **values})
    return {"runs": runs, "comparisons": comparisons}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = summarize(args.root)
    manifest = args.root / "manifest.json"
    report["manifest"] = json.loads(manifest.read_text())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n")
    lines = ["# PR 504 incremental collection measurements", "",
             "Two executions per revision/mode; each execution has three samples. Times are the median of the two execution means. "
             "All samples and repetition means are preserved in the adjacent JSON. These are method costs, not process RSS or whole-service throughput.", "",
             "| Mode | Case | Before ns | After ns | Time delta | Before B/op | After B/op |",
             "|---|---|---:|---:|---:|---:|---:|"]
    for row in report["comparisons"]:
        before, after = row["baseline"], row["candidate"]
        delta = (after["Nanoseconds"] / before["Nanoseconds"] - 1) * 100 if before["Nanoseconds"] else 0
        lines.append(f'| {row["mode"]} | {row["case"]} | {before["Nanoseconds"]:.2f} | {after["Nanoseconds"]:.2f} | '
                     f'{delta:+.1f}% | {before["Bytes"]:.1f} | {after["Bytes"]:.1f} |')
    args.output.with_suffix(".md").write_text("\n".join(lines) + "\n")
    print(f"Validated {len(report['runs'])} executions and {len(report['comparisons'])} mode/case comparisons")


if __name__ == "__main__":
    main()
