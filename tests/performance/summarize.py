"""Summarizes the baseline benchmark runs in tests/performance/.results/baseline into one readable report (Step 14C).

Reads the k6 summary exports ("<kind>-c<clients>-i<n>.json"), the resource samples ("resources-*.csv") and the database statement
counts, and prints: per iteration p50/p95/p99, throughput, the exact percentage of requests under the NFR4 threshold, and the
successful / expected-conflict / unexpected-error counts; then the median and worst iteration, and resource usage.
Only the measured phase is reported (the warm-up is excluded). Run with "perf.sh report".
"""
import csv
import glob
import json
import os
import re
import statistics
import sys

THRESHOLD = {"reads": ("read_under_300ms", 300, "read"), "reservations": ("reservation_under_750ms", 750, "reservation")}


def metric(metrics, name):
    return metrics.get(name, {})


def load_iteration(path, kind):
    data = json.load(open(path))["metrics"]
    rate_name = THRESHOLD[kind][0]
    duration = metric(data, "http_req_duration{phase:measure,expected_response:true}")
    clock = metric(data, "measure_clock_ms{phase:measure}")
    window = max((clock.get("max", 0) - clock.get("min", 0)) / 1000.0, 0.001)
    ok = metric(data, "responses_successful{phase:measure}").get("count", 0)
    conflict = metric(data, "responses_expected_conflict{phase:measure}").get("count", 0)
    errors = metric(data, "responses_unexpected_error{phase:measure}").get("count", 0)
    total = ok + conflict + errors
    rate = metric(data, f"{rate_name}{{phase:measure}}")
    fast = rate.get("passes", 0)
    endpoints = {}
    for key, value in data.items():
        found = re.match(r"http_req_duration\{phase:measure,name:([a-z-]+),expected_response:true\}", key)
        if found:
            endpoints[found.group(1)] = value
    return {
        "p50": duration.get("med"), "p95": duration.get("p(95)"), "p99": duration.get("p(99)"), "max": duration.get("max"),
        "requests": total, "ok": ok, "conflict": conflict, "errors": errors, "window": window,
        "throughput": total / window,
        "fast_of_successful": 100.0 * fast / ok if ok else 0.0,       # the NFR4 reading: failed requests are not counted as fast
        "fast_of_all": 100.0 * fast / total if total else 0.0,        # the strict reading: a failed request counts as NOT fast
        "success_pct": 100.0 * ok / total if total else 0.0,
        "endpoints": endpoints,
    }


def resources(path):
    rows = list(csv.DictReader(open(path)))
    if not rows:
        return None
    def col(name):
        values = [float(r[name]) for r in rows if r.get(name) not in (None, "")]
        return (statistics.mean(values), max(values)) if values else (0.0, 0.0)
    return {name: col(name) for name in ("api_cpu_pct", "api_rss_mb", "k6_cpu_pct", "db_cpu_pct", "db_mem_mb")}


def fmt(value, digits=1):
    return "-" if value is None else f"{value:.{digits}f}"


def main(directory):
    runs = {}
    for path in sorted(glob.glob(os.path.join(directory, "*-c*-i*.json"))):
        found = re.match(r"(reads|reservations)-c(\d+)-i(\d+)\.json$", os.path.basename(path))
        if found:
            runs.setdefault((found.group(1), int(found.group(2))), []).append((int(found.group(3)), path))
    if not runs:
        sys.exit("no baseline results found: run perf.sh bench first")

    for (kind, clients), items in sorted(runs.items()):
        _, limit, noun = THRESHOLD[kind]
        print(f"\n### {kind}: {clients} concurrent client(s), {len(items)} measured iteration(s)\n")
        print(f"| iter | requests | success % | conflicts | errors | p50 ms | p95 ms | p99 ms | max ms | req/s | % of successful {noun}s < {limit} ms | % of all < {limit} ms |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|")
        results = []
        for index, path in items:
            r = load_iteration(path, kind)
            results.append(r)
            print(f"| {index} | {r['requests']} | {r['success_pct']:.2f} | {r['conflict']} | {r['errors']} | {fmt(r['p50'])} | {fmt(r['p95'])} | {fmt(r['p99'])} | {fmt(r['max'])} "
                  f"| {r['throughput']:.0f} | {r['fast_of_successful']:.3f} | {r['fast_of_all']:.3f} |")
        median = lambda key: statistics.median(x[key] for x in results)
        worst = lambda key, fn=max: fn(x[key] for x in results)
        print(f"| **median** | | {median('success_pct'):.2f} | | | {fmt(median('p50'))} | {fmt(median('p95'))} | {fmt(median('p99'))} | | {median('throughput'):.0f} | {median('fast_of_successful'):.3f} | {median('fast_of_all'):.3f} |")
        print(f"| **worst** | | {worst('success_pct', min):.2f} | | {worst('errors')} | {fmt(worst('p50'))} | {fmt(worst('p95'))} | {fmt(worst('p99'))} | {fmt(worst('max'))} | {worst('throughput', min):.0f} | {worst('fast_of_successful', min):.3f} | {worst('fast_of_all', min):.3f} |")

        if kind == "reads":
            print("\nPer endpoint (median over iterations, ms):\n")
            print("| endpoint | p50 | p95 | p99 |")
            print("|---|---|---|---|")
            names = sorted({n for r in results for n in r["endpoints"]})
            for name in names:
                vals = [r["endpoints"][name] for r in results if name in r["endpoints"]]
                print(f"| {name} | {fmt(statistics.median(v['med'] for v in vals), 2)} | {fmt(statistics.median(v['p(95)'] for v in vals), 2)} | {fmt(statistics.median(v['p(99)'] for v in vals), 2)} |")

        usage = [resources(os.path.join(directory, f"resources-{kind}-c{clients}-i{index}.csv")) for index, _ in items if os.path.exists(os.path.join(directory, f"resources-{kind}-c{clients}-i{index}.csv"))]
        usage = [u for u in usage if u]
        if usage:
            def agg(key):
                means = statistics.mean(u[key][0] for u in usage)
                peak = max(u[key][1] for u in usage)
                return f"{means:.0f} / {peak:.0f}"
            print(f"\nResources (mean / peak over the runs, warm-up included; CPU % of one core): API cpu {agg('api_cpu_pct')}, API memory {agg('api_rss_mb')} MB, "
                  f"k6 cpu {agg('k6_cpu_pct')}, database cpu {agg('db_cpu_pct')}, database memory {agg('db_mem_mb')} MB")


if __name__ == "__main__":
    main(sys.argv[1])
