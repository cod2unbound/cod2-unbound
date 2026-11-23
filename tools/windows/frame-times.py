#!/usr/bin/env python3
"""Summarize Windows client QPC traces and PresentMon CSV by frame duration."""

import argparse
from collections import defaultdict
import csv
import json
import math
from pathlib import Path
import statistics
import struct

TRACE_HEADER = struct.Struct("<8sQQ")
TRACE_SAMPLE = struct.Struct("<QQ")
TRACE_MAGIC = b"COD2QPC1"
TRACE_CAPACITY = 262144
PRESENT_TIME_FIELDS = ("TimeInQPC", "QPCTime")


def read_engine(path, start_qpc, end_qpc):
    with path.open("rb") as stream:
        header = stream.read(TRACE_HEADER.size)
        if len(header) != TRACE_HEADER.size:
            raise ValueError("incomplete QPC trace header")
        magic, frequency, total = TRACE_HEADER.unpack(header)
        if magic != TRACE_MAGIC or frequency == 0:
            raise ValueError("unrecognized QPC trace header")
        payload = stream.read()
    count = min(total, TRACE_CAPACITY)
    if len(payload) != count * TRACE_SAMPLE.size:
        raise ValueError("incomplete QPC trace or invalid sample count")
    if count < 2:
        raise ValueError("QPC trace contains fewer than two successful presents")

    frame_ms, present_ms = [], []
    tick_ms = 1000 / frequency
    previous = None
    for submit, duration in TRACE_SAMPLE.iter_unpack(payload):
        if previous is not None and submit <= previous:
            raise ValueError("QPC trace timestamps are out of order")
        if previous is not None and in_range(previous, start_qpc, end_qpc) and in_range(submit, start_qpc, end_qpc):
            frame_ms.append((submit - previous) * tick_ms)
            present_ms.append(duration * tick_ms)
        previous = submit
    return frame_ms, present_ms, {"frequency": frequency, "overwritten_samples": total - count}


def in_range(value, start, end):
    return (start is None or value >= start) and (end is None or value <= end)


def read_presentmon(path, start_qpc, end_qpc, pid):
    swap_chains = defaultdict(list)
    last_present = {}
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = csv.DictReader(stream)
        fields = set(rows.fieldnames or ())
        required = {"MsBetweenPresents", "ProcessID", "SwapChainAddress"}
        if not required <= fields:
            raise ValueError("expected PresentMon CSV with MsBetweenPresents, ProcessID and SwapChainAddress")
        time_field = next((name for name in PRESENT_TIME_FIELDS if name in fields), None)
        bounded = start_qpc is not None or end_qpc is not None
        if bounded and time_field is None:
            raise ValueError("QPC bounds require PresentMon --qpc_time")
        for row in rows:
            process = int(row["ProcessID"])
            if pid is not None and process != pid:
                continue
            chain = process, row["SwapChainAddress"]
            if bounded:
                current = int(row[time_field])
                previous = last_present.get(chain)
                last_present[chain] = current
                if previous is None or not in_range(previous, start_qpc, end_qpc) or not in_range(current, start_qpc, end_qpc):
                    continue
            elapsed = float(row["MsBetweenPresents"] or "nan")
            if elapsed > 0 and math.isfinite(elapsed):
                swap_chains[chain].append(row)

    processes = {process for process, _ in swap_chains}
    if len(processes) != 1:
        raise ValueError("expected one captured process with presents, or specify --pid")
    (process, address), rows = max(swap_chains.items(), key=lambda item: len(item[1]))
    presents = [float(row["MsBetweenPresents"]) for row in rows]
    duration = [float(row["MsInPresentAPI"]) for row in rows if row.get("MsInPresentAPI", "") not in ("", "NA")]
    metadata = {
        "pid": process,
        "swap_chain": address,
        "other_swap_chain_samples": sum(map(len, swap_chains.values())) - len(rows),
        "present_modes": sorted({row["PresentMode"] for row in rows if "PresentMode" in row}),
        "sync_intervals": sorted({row["SyncInterval"] for row in rows if "SyncInterval" in row}),
    }
    return presents, duration, metadata


def percentile(sorted_values, fraction):
    return sorted_values[math.ceil(len(sorted_values) * fraction) - 1]


def slow_tail_mean(sorted_values, fraction):
    count = math.ceil(len(sorted_values) * fraction)
    return statistics.mean(sorted_values[-count:])


def periodic_hitches(values, threshold):
    elapsed = 0
    events = []
    for interval in values:
        elapsed += interval
        if interval >= threshold:
            events.append(elapsed)

    # Ignore intra-burst gaps and look for a repeatedly observed spacing.
    starts = []
    for index, event in enumerate(events):
        if index == 0 or event - events[index - 1] >= 75:
            starts.append(event)
    gaps = [second - first for first, second in zip(starts, starts[1:])]
    repeat = None
    if len(gaps) >= 4:
        period = statistics.median(gaps)
        similar = sum(abs(gap - period) <= max(10, period * 0.05) for gap in gaps)
        if similar >= 4 and similar / len(gaps) >= 0.75:
            repeat = round(period, 3)
    return {"threshold_ms": threshold, "count": len(events), "burst_count": len(starts), "repeat_ms": repeat}


def analyze(values, duration, cap):
    if not values:
        raise ValueError("no intervals in the requested sample range")
    ordered = sorted(values)
    # The legacy limiter truncates 1000/cap to an integer number of milliseconds.
    target = max(1, 1000 // cap) if cap else None
    threshold = max(10, target * 2 if target is not None else statistics.median(values) * 2)
    results = {
        "intervals": len(values),
        "span_seconds": round(sum(values) / 1000, 3),
        "mean_ms": round(statistics.mean(values), 3),
        "median_ms": round(statistics.median(values), 3),
        "p99_ms": round(percentile(ordered, 0.99), 3),
        "p99_9_ms": round(percentile(ordered, 0.999), 3),
        "slowest_1pct_mean_ms": round(slow_tail_mean(ordered, 0.01), 3),
        "slowest_0_1pct_mean_ms": round(slow_tail_mean(ordered, 0.001), 3),
        "longest_ms": round(ordered[-1], 3),
        "target_ms": target,
        "within_0_5ms_pct": None,
        "hitches": periodic_hitches(values, threshold),
    }
    if target is not None:
        results["within_0_5ms_pct"] = round(sum(abs(value - target) <= 0.5 for value in values) * 100 / len(values), 2)
    if duration:
        results["in_present_p99_ms"] = round(percentile(sorted(duration), 0.99), 3)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--source", choices=("engine", "presentmon"), required=True)
    parser.add_argument("--cap", type=int, default=0, help="effective com_maxfps (0 for uncapped)")
    parser.add_argument("--pid", type=int, help="filter a PresentMon capture by process ID")
    parser.add_argument("--qpc-start", type=int)
    parser.add_argument("--qpc-end", type=int)
    args = parser.parse_args()
    if args.cap < 0 or (args.qpc_start is not None and args.qpc_end is not None and args.qpc_start >= args.qpc_end):
        parser.error("cap must be nonnegative and QPC bounds must be increasing")
    try:
        if args.source == "engine":
            values, duration, metadata = read_engine(args.capture, args.qpc_start, args.qpc_end)
        else:
            values, duration, metadata = read_presentmon(args.capture, args.qpc_start, args.qpc_end, args.pid)
        print(json.dumps({**analyze(values, duration, args.cap), **metadata}, indent=2, allow_nan=False))
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
