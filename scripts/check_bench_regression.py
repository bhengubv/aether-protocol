#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Aether benchmark regression gate.

Usage:
    python3 scripts/check_bench_regression.py \\
        --lang go \\
        --current /tmp/go_bench_current.txt \\
        --baseline bench/baselines/go.txt \\
        [--threshold 0.20]

Exits 0 if no case regresses by more than THRESHOLD (default 20 %).
Exits 1 on regression; exits 0 with a warning when the baseline is PENDING.

Supported --lang values and expected input formats
--------------------------------------------------
go          go test -bench=. -benchmem output
            "BenchmarkX25519Agree-8     12345678     97.8 ns/op ..."

rust        cargo bench --bench aether output
            "x25519_agree  time:  [97.8 ns  98.0 ns  98.2 ns]"

python      pytest-benchmark JSON (--benchmark-json=FILE)
            {"benchmarks":[{"name":"...","stats":{"median":1.23e-7}},...]}

typescript  npx tsx bench output; one line per case:
            "x25519_agree: 98.21 ns/op"

csharp      BenchmarkDotNet custom export; one line per case:
            "x25519_agree | 98.21 ns"

kotlin      JMH output:
            "aethermesh.bench.Benchmarks.x25519_agree  thrpt  N  12345.678 ops/s"

swift       XCTest measure output:
            "x25519_agree: measured [0.000000098 s]"

c           aethermesh_bench wall-clock output; one line per case:
            "x25519_agree: 98 ns"
"""

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Optional

THRESHOLD_DEFAULT = 0.20  # 20 % regression threshold


# ── parsers ──────────────────────────────────────────────────────────────────

def _ns(value: float, unit: str) -> float:
    """Convert a (value, unit) pair to nanoseconds."""
    unit = unit.strip().lower()
    if unit in ("ns", "ns/op"):
        return value
    if unit in ("µs", "us", "µs/op", "us/op"):
        return value * 1_000
    if unit in ("ms", "ms/op"):
        return value * 1_000_000
    if unit in ("s", "s/op"):
        return value * 1_000_000_000
    raise ValueError(f"Unknown time unit: {unit!r}")


def parse_go(text: str) -> dict[str, float]:
    """
    Parse `go test -bench=. -benchmem` output.
    Returns {normalised_name: median_ns}.
    Strips the -N CPU-count suffix and lowercases the name.
    """
    results: dict[str, float] = {}
    # e.g. "BenchmarkX25519Agree-8\t12345678\t97.8 ns/op\t0 B/op\t0 allocs/op"
    pattern = re.compile(
        r"^Benchmark(\w+?)(?:-\d+)?\s+\d+\s+(\d+(?:\.\d+)?)\s+(ns/op|µs/op|us/op|ms/op|s/op)"
    )
    for line in text.splitlines():
        m = pattern.match(line.strip())
        if m:
            name = _normalise_name(m.group(1))
            ns = _ns(float(m.group(2)), m.group(3))
            results[name] = ns
    return results


def parse_rust(text: str) -> dict[str, float]:
    """
    Parse criterion `cargo bench` output.
    Each measured case produces a line like:
        x25519_agree            time:   [97.821 ns 98.012 ns 98.234 ns]
    The middle value is the median estimate.
    """
    results: dict[str, float] = {}
    # criterion output: name  time:  [lo  median  hi]
    pattern = re.compile(
        r"^(\S+)\s+time:\s+\[\S+\s+(\d+(?:\.\d+)?)\s+(ns|µs|ms|s)\s+\S+\s+(ns|µs|ms|s)\s*\]"
    )
    # simpler fallback pattern (single unit for all three values)
    pattern2 = re.compile(
        r"^(\S+)\s+time:\s+\[(?:\S+\s+){0,2}(\d+(?:\.\d+)?)\s+(ns|µs|ms|s)\s"
    )
    for line in text.splitlines():
        line = line.strip()
        m = pattern.match(line)
        if m:
            name = _normalise_name(m.group(1))
            # median unit is group(4) but may not exist; try group(3) first
            try:
                ns = _ns(float(m.group(2)), m.group(3))
            except ValueError:
                continue
            results[name] = ns
            continue
        m2 = pattern2.match(line)
        if m2:
            name = _normalise_name(m2.group(1))
            ns = _ns(float(m2.group(2)), m2.group(3))
            results[name] = ns
    return results


def parse_python(text: str) -> dict[str, float]:
    """
    Parse pytest-benchmark JSON (--benchmark-json=FILE).
    stats.median is in seconds; convert to ns.
    """
    results: dict[str, float] = {}
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        print(f"[warn] could not parse Python benchmark JSON: {exc}", file=sys.stderr)
        return results
    for bench in data.get("benchmarks", []):
        name = _normalise_name(bench.get("name", ""))
        median_s = bench.get("stats", {}).get("median", 0.0)
        results[name] = median_s * 1_000_000_000
    return results


def parse_typescript(text: str) -> dict[str, float]:
    """
    Parse tinybench / custom bench.ts output.
    Expected format (one case per line):
        x25519_agree: 98.21 ns/op
    """
    results: dict[str, float] = {}
    pattern = re.compile(
        r"^(\S+?):\s+(\d+(?:\.\d+)?)\s+(ns/op|µs/op|us/op|ms/op|s/op)"
    )
    for line in text.splitlines():
        m = pattern.match(line.strip())
        if m:
            name = _normalise_name(m.group(1))
            ns = _ns(float(m.group(2)), m.group(3))
            results[name] = ns
    return results


def parse_csharp(text: str) -> dict[str, float]:
    """
    Parse BenchmarkDotNet custom one-line-per-case output:
        x25519_agree | 98.21 ns
    Also handles the default BenchmarkDotNet summary table:
        | x25519_agree |  98.21 ns | ...
    """
    results: dict[str, float] = {}
    # pipe-separated custom export
    pattern = re.compile(
        r"^\|?\s*(\S+?)\s*\|\s*(\d+(?:\.\d+)?)\s+(ns|µs|ms|s)\s*(?:\||$)"
    )
    # simple "name: value unit" fallback
    simple = re.compile(
        r"^(\S+)\s*[:|]\s*(\d+(?:\.\d+)?)\s+(ns|µs|ms|s)"
    )
    for line in text.splitlines():
        line = line.strip()
        m = pattern.match(line)
        if m:
            name = _normalise_name(m.group(1))
            ns = _ns(float(m.group(2)), m.group(3))
            results[name] = ns
            continue
        m2 = simple.match(line)
        if m2:
            name = _normalise_name(m2.group(1))
            ns = _ns(float(m2.group(2)), m2.group(3))
            results[name] = ns
    return results


def parse_kotlin(text: str) -> dict[str, float]:
    """
    Parse JMH output.
    JMH reports ops/s by default; convert to ns/op.
    Typical line:
        aethermesh.bench.Benchmarks.x25519Agree  thrpt   5  12345678.000 ± 12345.000  ops/s
    """
    results: dict[str, float] = {}
    pattern = re.compile(
        r"^(?:\S+\.)?(\w+)\s+thrpt\s+\d+\s+(\d+(?:\.\d+)?)\s+±\s+\S+\s+ops/s"
    )
    # Also handle avgt (average time in some configured unit):
    avgt = re.compile(
        r"^(?:\S+\.)?(\w+)\s+avgt\s+\d+\s+(\d+(?:\.\d+)?)\s+±\s+\S+\s+(ns|µs|ms|s)/op"
    )
    for line in text.splitlines():
        line = line.strip()
        m = avgt.match(line)
        if m:
            name = _normalise_name(m.group(1))
            ns = _ns(float(m.group(2)), m.group(3) + "/op")
            results[name] = ns
            continue
        m2 = pattern.match(line)
        if m2:
            name = _normalise_name(m2.group(1))
            ops_per_s = float(m2.group(2))
            if ops_per_s > 0:
                results[name] = 1_000_000_000 / ops_per_s
    return results


def parse_swift(text: str) -> dict[str, float]:
    """
    Parse XCTest `measure {}` output.
    Typical line produced by our bench harness:
        x25519_agree: measured [0.000000098 s]
    """
    results: dict[str, float] = {}
    pattern = re.compile(
        r"^(\S+?):\s+measured\s+\[(\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s+s\]"
    )
    for line in text.splitlines():
        m = pattern.match(line.strip())
        if m:
            name = _normalise_name(m.group(1))
            ns = float(m.group(2)) * 1_000_000_000
            results[name] = ns
    return results


def parse_c(text: str) -> dict[str, float]:
    """
    Parse aethermesh_bench wall-clock output.
    Expected format (one case per line):
        x25519_agree: 98 ns
    """
    results: dict[str, float] = {}
    pattern = re.compile(
        r"^(\S+?):\s+(\d+(?:\.\d+)?)\s+(ns|µs|us|ms|s)"
    )
    for line in text.splitlines():
        m = pattern.match(line.strip())
        if m:
            name = _normalise_name(m.group(1))
            ns = _ns(float(m.group(2)), m.group(3))
            results[name] = ns
    return results


PARSERS = {
    "go": parse_go,
    "rust": parse_rust,
    "python": parse_python,
    "typescript": parse_typescript,
    "csharp": parse_csharp,
    "kotlin": parse_kotlin,
    "swift": parse_swift,
    "c": parse_c,
}


# ── baseline file I/O ─────────────────────────────────────────────────────────

def _normalise_name(name: str) -> str:
    """Lower-case and strip common Go/Java/Swift prefixes for stable comparison."""
    name = name.lower()
    # Go: "X25519Agree" → "x25519agree"; already lower-cased
    # Strip common package prefixes
    for prefix in ("benchmark", "bench_", "bench", "test_", "test"):
        if name.startswith(prefix) and len(name) > len(prefix):
            name = name[len(prefix):]
            break
    # Replace non-alphanumeric with underscores for uniform matching
    name = re.sub(r"[^a-z0-9]+", "_", name).strip("_")
    return name


def load_baseline(path: Path) -> Optional[dict[str, float]]:
    """
    Load a baseline file.
    Returns None if the file is PENDING (not yet established).
    Returns a dict of {name: ns} otherwise.
    """
    if not path.exists():
        return None
    text = path.read_text(encoding="utf-8")
    first_data_line = next(
        (ln.strip() for ln in text.splitlines() if ln.strip() and not ln.strip().startswith("#")),
        ""
    )
    if first_data_line.upper() == "PENDING":
        return None
    results: dict[str, float] = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" in line:
            k, _, v = line.partition("=")
            try:
                results[k.strip()] = float(v.strip())
            except ValueError:
                pass
    return results


def save_baseline(path: Path, results: dict[str, float]) -> None:
    """Write a baseline file in the canonical key=ns format."""
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["# Generated by scripts/check_bench_regression.py  --save-baseline\n"]
    for name, ns in sorted(results.items()):
        lines.append(f"{name}={ns:.3f}\n")
    path.write_text("".join(lines), encoding="utf-8")


# ── comparison ────────────────────────────────────────────────────────────────

def compare(
    current: dict[str, float],
    baseline: dict[str, float],
    threshold: float,
) -> list[str]:
    """
    Return a list of failure messages (empty = pass).
    A case 'passes' if current_ns / baseline_ns <= 1 + threshold.
    """
    failures: list[str] = []
    for name, cur_ns in sorted(current.items()):
        if name not in baseline:
            # new case — no historical data to compare against
            continue
        base_ns = baseline[name]
        if base_ns <= 0:
            continue
        ratio = cur_ns / base_ns
        pct = (ratio - 1.0) * 100
        status = "PASS" if ratio <= 1.0 + threshold else "FAIL"
        print(
            f"  {status}  {name:45s}  "
            f"current={cur_ns:>10.1f} ns  baseline={base_ns:>10.1f} ns  "
            f"delta={pct:+.1f}%"
        )
        if status == "FAIL":
            failures.append(
                f"{name}: {cur_ns:.1f} ns vs baseline {base_ns:.1f} ns "
                f"({pct:+.1f}%, threshold ±{threshold*100:.0f}%)"
            )
    return failures


# ── CLI ───────────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description="Aether benchmark regression gate")
    ap.add_argument("--lang", required=True, choices=list(PARSERS), help="Language tag")
    ap.add_argument("--current", required=True, help="Path to current bench output file")
    ap.add_argument("--baseline", required=True, help="Path to baseline file")
    ap.add_argument(
        "--threshold", type=float, default=THRESHOLD_DEFAULT,
        help=f"Regression threshold (default {THRESHOLD_DEFAULT:.0%})"
    )
    ap.add_argument(
        "--save-baseline", action="store_true",
        help="Write current results to --baseline instead of comparing"
    )
    args = ap.parse_args()

    current_text = Path(args.current).read_text(encoding="utf-8")
    current = PARSERS[args.lang](current_text)

    if not current:
        print(f"[warn] No benchmark results parsed from {args.current} (lang={args.lang})")
        return 0

    if args.save_baseline:
        save_baseline(Path(args.baseline), current)
        print(f"Baseline written to {args.baseline} ({len(current)} cases)")
        return 0

    baseline = load_baseline(Path(args.baseline))

    if baseline is None:
        print(
            f"[info] Baseline for {args.lang} is PENDING "
            f"— run `bash scripts/update_baselines.sh` to establish it."
        )
        return 0

    print(f"Comparing {args.lang} benchmarks against {args.baseline} "
          f"(threshold +{args.threshold:.0%}):")
    failures = compare(current, baseline, args.threshold)

    if failures:
        print(f"\n[FAIL] {len(failures)} benchmark(s) regressed beyond threshold:")
        for f in failures:
            print(f"  • {f}")
        return 1

    print(f"\n[PASS] All {len(current)} cases within threshold.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
