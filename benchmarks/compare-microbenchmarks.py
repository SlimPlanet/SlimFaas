#!/usr/bin/env python3
"""Merge two BenchmarkDotNet brief JSON exports into a before/after Markdown table.

Usage:
  benchmarks/compare-microbenchmarks.py --baseline <dir-or-file> --candidate <dir-or-file>
      [--baseline-label v0.74.0] [--candidate-label HEAD] [--output micro-comparison.md]

A directory is scanned recursively for *-report-brief.json (the export enabled in
benchmarks/SlimFaas.Benchmarks/Program.cs). Benchmarks are matched on
"<Type>.<Method>(<Parameters>)"; the ones present in only one export are listed
separately (a subject that did not exist yet in the baseline, for example).
"""
import argparse
import glob
import json
import os
import sys


def load(path):
    files = [path] if os.path.isfile(path) else sorted(
        glob.glob(os.path.join(path, "**", "*-report-brief.json"), recursive=True))
    if not files:
        sys.exit(f"no BenchmarkDotNet brief JSON export found under {path}")
    rows = {}
    for file in files:
        with open(file, encoding="utf-8-sig") as handle:
            report = json.load(handle)
        for bench in report.get("Benchmarks", []):
            params = bench.get("Parameters") or ""
            key = f"{bench['Type']}.{bench['Method']}({params})"
            stats = bench.get("Statistics") or {}
            memory = bench.get("Memory") or {}
            rows[key] = {
                "type": bench["Type"],
                "method": bench["Method"],
                "params": params,
                "mean_ns": stats.get("Mean"),
                "stddev_ns": stats.get("StandardDeviation"),
                "alloc_b": memory.get("BytesAllocatedPerOperation"),
            }
    return rows


def fmt_time(ns):
    if ns is None:
        return "n/a"
    if ns < 1_000:
        return f"{ns:,.2f} ns"
    if ns < 1_000_000:
        return f"{ns / 1_000:,.2f} µs"
    return f"{ns / 1_000_000:,.3f} ms"


def fmt_alloc(b):
    if b is None:
        return "n/a"
    if b < 1024:
        return f"{b:,.0f} B"
    return f"{b / 1024:,.2f} KB"


def ratio(before, after):
    if before is None or after is None:
        return "n/a"
    if after == 0:
        return "∞" if before > 0 else "1.00×"
    if before == 0:
        return "0.00×"
    return f"{before / after:,.2f}×"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--baseline-label", default="baseline")
    parser.add_argument("--candidate-label", default="candidate")
    parser.add_argument("--output")
    args = parser.parse_args()

    before = load(args.baseline)
    after = load(args.candidate)
    common = [key for key in after if key in before]
    only_before = [key for key in before if key not in after]
    only_after = [key for key in after if key not in before]

    lines = [
        f"| Benchmark | Params | {args.baseline_label} mean / alloc | {args.candidate_label} mean / alloc | Time gain | Alloc gain |",
        "|---|---|---:|---:|---:|---:|",
    ]
    current_type = None
    for key in sorted(common, key=lambda k: (before[k]["type"], before[k]["method"], before[k]["params"])):
        b, a = before[key], after[key]
        if b["type"] != current_type:
            current_type = b["type"]
        lines.append(
            f"| {b['type']}.{b['method']} | {b['params'] or '—'} "
            f"| {fmt_time(b['mean_ns'])} / {fmt_alloc(b['alloc_b'])} "
            f"| {fmt_time(a['mean_ns'])} / {fmt_alloc(a['alloc_b'])} "
            f"| {ratio(b['mean_ns'], a['mean_ns'])} | {ratio(b['alloc_b'], a['alloc_b'])} |")

    def only(label, keys, rows):
        if not keys:
            return []
        out = ["", f"Only in {label}:", "", "| Benchmark | Params | Mean | Allocated |", "|---|---|---:|---:|"]
        for key in sorted(keys):
            r = rows[key]
            out.append(f"| {r['type']}.{r['method']} | {r['params'] or '—'} | {fmt_time(r['mean_ns'])} | {fmt_alloc(r['alloc_b'])} |")
        return out

    lines += only(args.baseline_label, only_before, before)
    lines += only(args.candidate_label, only_after, after)
    lines.append("")
    lines.append("Time gain = baseline mean / candidate mean (>1 = faster now); Alloc gain = baseline bytes / candidate bytes (>1 = fewer allocations now).")
    text = "\n".join(lines) + "\n"
    if args.output:
        os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
        with open(args.output, "w", encoding="utf-8") as handle:
            handle.write(text)
    sys.stdout.write(text)


if __name__ == "__main__":
    main()
