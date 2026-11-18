#!/usr/bin/env python3
"""Summarize every completed repetition without pooling latency samples."""

import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
import statistics


def median(rows, field):
    return statistics.median(row[field] for row in rows)


def summarize(root):
    manifest = json.loads((root / "manifest.json").read_text())
    cases = defaultdict(list)
    direct = defaultdict(list)
    scaling = defaultdict(list)
    focused = defaultdict(list)
    startup = defaultdict(list)
    raw = {}
    variants = manifest["settings"]["variants"].split(",")
    analysis_path = root / "analysis.json"
    analysis = json.loads(analysis_path.read_text()) if analysis_path.exists() else {}
    overrides = analysis.get("rounds_by_variant", {})
    planned = manifest["settings"]["rounds"]
    expected = {variant: overrides.get(variant, planned) for variant in variants}
    if set(overrides) - set(variants) or any(type(count) is not int or not 1 <= count <= planned
                                           for count in expected.values()):
        raise ValueError("Invalid explicit repetition counts")
    if overrides and not analysis.get("reason"):
        raise ValueError("Explicit repetition counts require an analysis reason")
    for mode in ["jit", "aot"]:
        if expected.get("baseline-" + mode) != expected.get("candidate-" + mode):
            raise ValueError(f"Unbalanced before/after repetition counts: {mode}")
    unsuccessful = [json.loads(path.read_text())
                    for path in sorted(root.glob("round-*.incomplete-*/failure.json"))]
    expected_http = {(mode, payload, concurrency)
                     for mode in ["direct", "sync", "async"]
                     for payload in map(int, manifest["settings"]["payloads"].split(","))
                     for concurrency in map(int, manifest["settings"]["concurrency"].split(","))}
    for variant in variants:
        for repetition in range(1, expected[variant] + 1):
            directory = root / f"round-{repetition}-{variant}"
            if not (directory / "complete.json").exists():
                raise ValueError(f"Incomplete repetition: {directory}")
            report = json.loads((directory / "results.json").read_text())
            settings = report["Settings"]
            if (settings["DurationSeconds"] != manifest["settings"]["duration"] or
                    settings["WarmupSeconds"] != manifest["settings"]["warmup"] or
                    settings["Repetitions"] != 1):
                raise ValueError(f"Mismatched measurement settings: {directory}")
            observed_http = {(run["Mode"], run["PayloadBytes"], run["Concurrency"])
                             for run in report["LatencyRuns"]}
            if observed_http != expected_http or len(report["LatencyRuns"]) != len(expected_http):
                raise ValueError(f"Missing, duplicated or unexpected HTTP cases: {directory}")
            raw[directory.name] = {"http": report, "data": {}}
            readiness = json.loads((directory / "startup.json").read_text())
            idle = json.loads((directory / "idle.json").read_text())
            raw[directory.name].update(startup=readiness, idle=idle)
            startup[variant].append({"ready_seconds": readiness["ready_seconds"],
                                     "rss_mib": idle["rss_bytes"] / 1048576})
            for run in report["LatencyRuns"]:
                if run["Failed"] or run["AsyncMissing"] or run["AsyncDuplicates"]:
                    raise ValueError(f"Correctness failure: {directory} {run}")
                key = (variant, run["Mode"], run["PayloadBytes"], run["Concurrency"])
                if run["Mode"] == "direct":
                    direct[key].append(run["RatePerSecond"])
                    continue
                resources = run["Resources"]
                cases[key].append({
                    "rate": run["RatePerSecond"], "p50_ms": run["P50Milliseconds"],
                    "p95_ms": run["P95Milliseconds"], "p99_ms": run["P99Milliseconds"],
                    "arrival_p95_ms": (run.get("AsyncDelivery") or {}).get("ArrivalP95Milliseconds", 0),
                    "cpu_ms": resources["CpuMillisecondsPerRequest"],
                    "rss_mib": resources["PeakWorkingSetBytes"] / 1048576,
                    "completed": run["Completed"],
                })
            for workload in report["AsyncWorkloads"]:
                if workload["Failed"] or workload["Missing"] or workload["Duplicates"]:
                    raise ValueError(f"Async correctness failure: {directory}")
                focused[(variant, workload["LoadShape"])].append(workload)
            if report["Scaling"]["Failed"] or report["Scaling"]["TimedOut"]:
                raise ValueError(f"Scaling failure: {directory}")
            scaling[variant].append(report["Scaling"])
            for path in sorted(directory.glob("*/summary.json")):
                data = json.loads(path.read_text())
                if data.get("warmup"):
                    continue
                raw[directory.name]["data"][path.parent.name] = data
                if data["scenario"] == "streams":
                    if (data["delivery"]["uniqueCount"] != manifest["settings"]["stream_messages"] or
                            data["delivery"]["duplicateCount"] or
                            any(data["progress"][field] for field in ["Lag", "PendingCount", "FailedCount"])):
                        raise ValueError(f"Stream correctness failure: {path}")
                    row = {"rate": data["rate"], "p50_ms": data["p50_ms"],
                           "p95_ms": data["p95_ms"], "p99_ms": data["p99_ms"],
                           "completed": data["completed"]}
                else:
                    load = data["load"]
                    if load["Failed"]:
                        raise ValueError(f"Data correctness failure: {path}")
                    latency = next(item for item in load["Operations"] if item["Operation"] == "All")
                    row = {"rate": load["Rate"], "p50_ms": latency["P50Milliseconds"],
                           "p95_ms": latency["P95Milliseconds"], "p99_ms": latency["P99Milliseconds"],
                           "completed": load["Completed"]}
                row.update(cpu_ms=data["cpu_ms_per_operation"], rss_mib=data["peak_rss_bytes"] / 1048576)
                payload = data.get("payload_bytes", 262144 if data["scenario"] == "files" else 4096)
                cases[(variant, data["scenario"], payload, manifest["settings"]["data_concurrency"])].append(row)

    expected_cases = set()
    for variant in variants:
        for workload in ["sync", "async"]:
            for payload in map(int, manifest["settings"]["payloads"].split(",")):
                for concurrency in map(int, manifest["settings"]["concurrency"].split(",")):
                    expected_cases.add((variant, workload, payload, concurrency))
        workloads = manifest["settings"]["data_scenarios"].split(",") if manifest["settings"]["data_scenarios"] else []
        if manifest["settings"]["stream_messages"]:
            workloads.append("streams")
        for workload in workloads:
            payload = manifest["settings"].get("file_bytes", 262144) if workload == "files" else 4096
            expected_cases.add((variant, workload, payload, manifest["settings"]["data_concurrency"]))
    if set(cases) != expected_cases:
        raise ValueError(f"Unexpected or missing cases: {set(cases) ^ expected_cases}")
    aggregates = {}
    for key, rows in sorted(cases.items()):
        if len(rows) != expected[key[0]]:
            raise ValueError(f"Missing case repetitions: {key}")
        if any(not math.isfinite(value) or value < 0 for row in rows for value in row.values()):
            raise ValueError(f"Invalid or decreasing resource counter: {key}")
        aggregates[key] = {field: median(rows, field) for field in rows[0] if field != "completed"}
        aggregates[key].update(repetitions=len(rows), rate_min=min(row["rate"] for row in rows),
                               rate_max=max(row["rate"] for row in rows), completed=sum(row["completed"] for row in rows))
        for field in ["cpu_ms", "rss_mib", "p95_ms"]:
            aggregates[key][field + "_min"] = min(row[field] for row in rows)
            aggregates[key][field + "_max"] = max(row[field] for row in rows)
    lines = ["# Local JIT / Native AOT before-and-after measurements", "",
             f"Function dispatch capacity: {manifest['settings'].get('dispatch_parallelism', 256)} globally and per target replica.", "",
             "All cells are medians across independent clean-cluster repetitions. CPU and RSS sum the three runtime nodes.",
             "RSS is the median sampled peak, not managed bytes allocated per operation. Data rows count workload operations.", "",
             f"Completed repetitions per revision: JIT {expected['baseline-jit']}; Native AOT {expected['baseline-aot']}. Original planned rounds: {planned}.",
             analysis.get("reason", ""), "",
             f"Archived unsuccessful attempts: {len(unsuccessful)}. Performance statistics cover completed repetitions only and do not establish reliability.", "",
             "| Runtime | N per revision | Workload | Bytes | C | Before op/s | After op/s | Rate Δ | Before p95 ms | After p95 ms | Before CPU ms/op | After CPU ms/op | Before RSS MiB | After RSS MiB |",
             "|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    comparisons = []
    for mode in ["jit", "aot"]:
        for key, before in aggregates.items():
            variant, workload, payload, concurrency = key
            if variant != "baseline-" + mode:
                continue
            after = aggregates[("candidate-" + mode, workload, payload, concurrency)]
            delta = (after["rate"] / before["rate"] - 1) * 100
            lines.append(f"| {mode} | {before['repetitions']} | {workload} | {payload} | {concurrency} | {before['rate']:.2f} | {after['rate']:.2f} | {delta:+.1f}% | {before['p95_ms']:.3f} | {after['p95_ms']:.3f} | {before['cpu_ms']:.4f} | {after['cpu_ms']:.4f} | {before['rss_mib']:.1f} | {after['rss_mib']:.1f} |")
            comparisons.append({"runtime": mode, "workload": workload, "payload": payload,
                                "concurrency": concurrency, "before": before, "after": after})
    lines += ["", "## Unchanged direct-target control", "",
              "| Runtime | Bytes | C | Before direct/s | After direct/s | Δ |",
              "|---|---:|---:|---:|---:|---:|"]
    for mode in ["jit", "aot"]:
        for key, values in sorted(direct.items()):
            variant, _, payload, concurrency = key
            if variant != "baseline-" + mode:
                continue
            before = statistics.median(values)
            after_values = direct[("candidate-" + mode, "direct", payload, concurrency)]
            count = expected[variant]
            if len(values) != count or len(after_values) != count:
                raise ValueError(f"Missing direct-control repetitions: {mode}, {payload}, {concurrency}")
            after = statistics.median(after_values)
            lines.append(f"| {mode} | {payload} | {concurrency} | {before:.1f} | {after:.1f} | {(after / before - 1) * 100:+.1f}% |")
    lines += ["", "## Fixed-count async scenarios", "",
              "Rates and HTTP p95 measure acceptance; arrival p95 includes queueing and dispatch.", "",
              "| Runtime | Shape | Before accepted/s | After accepted/s | Before HTTP p95 ms | After HTTP p95 ms | Before arrival p95 ms | After arrival p95 ms |",
              "|---|---|---:|---:|---:|---:|---:|---:|"]
    for mode in ["jit", "aot"]:
        shapes = sorted(shape for variant, shape in focused if variant == "baseline-" + mode)
        if shapes != ["burst", "paced"]:
            raise ValueError(f"Missing focused async shapes: {mode}")
        for shape in shapes:
            before = focused[("baseline-" + mode, shape)]
            after = focused[("candidate-" + mode, shape)]
            count = expected["baseline-" + mode]
            if len(before) != count or len(after) != count:
                raise ValueError(f"Missing focused async repetitions: {mode}, {shape}")
            arrival_before = statistics.median(row["Delivery"]["ArrivalP95Milliseconds"] for row in before)
            arrival_after = statistics.median(row["Delivery"]["ArrivalP95Milliseconds"] for row in after)
            lines.append(f"| {mode} | {shape} | {median(before, 'RatePerSecond'):.2f} | {median(after, 'RatePerSecond'):.2f} | {median(before, 'P95Milliseconds'):.3f} | {median(after, 'P95Milliseconds'):.3f} | {arrival_before:.3f} | {arrival_after:.3f} |")
    lines += ["", "## Scaling", "",
              "| Variant | First ready s | Four ready s | Drained s |",
              "|---|---:|---:|---:|"]
    for variant, rows in sorted(scaling.items()):
        lines.append(f"| {variant} | {median(rows, 'ReadyOneMilliseconds') / 1000:.3f} | {median(rows, 'ReadyTargetMilliseconds') / 1000:.3f} | {median(rows, 'QueueDrainedMilliseconds') / 1000:.3f} |")
    lines += ["", "## Cluster readiness and memory before load", "",
              "Readiness includes the local supervisor, Raft and the shared JIT target. RSS sums the runtime nodes after five seconds of settling.", "",
              "| Variant | Median ready s | Median pre-load RSS MiB |",
              "|---|---:|---:|"]
    for variant, rows in sorted(startup.items()):
        lines.append(f"| {variant} | {median(rows, 'ready_seconds'):.3f} | {median(rows, 'rss_mib'):.1f} |")
    return "\n".join(lines) + "\n", {"manifest": manifest, "analysis": analysis, "comparisons": comparisons, "runs": raw,
                                     "unsuccessful_attempts": unsuccessful}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    markdown, data = summarize(args.root)
    (args.root / "comparison.md").write_text(markdown)
    (args.root / "comparison.json").write_text(json.dumps(data, separators=(",", ":")) + "\n")
    print(markdown)


if __name__ == "__main__":
    main()
