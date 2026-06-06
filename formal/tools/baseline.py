#!/usr/bin/env python3
"""
Verification Regression Baseline + Checker
===========================================

Records the current verification state across all 20 models as a baseline
JSON file. Re-running with --check compares the current state against the
baseline and exits non-zero if any property regressed.

This is "pseudo-CI" — a local script that catches regressions without
violating the project's no-CI rule. Run it locally before commit; run it
in a Git pre-push hook; or run it on your Mac before submitting a paper.

Usage:
  python baseline.py --record         # Save current state as baseline
  python baseline.py --check          # Compare current to baseline
  python baseline.py --diff           # Show what changed

Captures per model:
  - Reachable state count
  - Conservation invariants (auto-discovered)
  - Per-place max marking
  - Goal reachability (true/false)
  - Safety violations (list)

A regression is any of:
  - Goal was reachable in baseline; not reachable now
  - Safety violation now where there was none before
  - A conservation invariant disappeared
  - Reachable state count drops significantly (model became degenerate)
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from verify import verify_model


BASELINE_FILE = Path(__file__).parent / "baseline.json"


def collect_current(formal_dir):
    """Run verification on all models, return summary dict."""
    results = {}
    for d in sorted(formal_dir.iterdir()):
        if not d.is_dir() or d.name in ("tools", "standards"):
            continue
        if not (d / f"{d.name}.pnml").exists():
            continue
        r = verify_model(d)
        if r:
            # Convert sets to sorted lists for stable JSON
            results[r["name"]] = {
                "reachable_states": r["reachable_states"],
                "invariants": [
                    {"expr": inv, "value": val} for inv, val in r["invariants"]
                ],
                "max_marks": {p: m for p, m in r["max_marks"].items()},
                "goal_reachable": r["goal_reachable"],
                "safety_violations": sorted(r["safety_violations"]),
                "places": r["places"],
                "transitions": r["transitions"],
            }
    return results


def record(formal_dir):
    """Save current state as baseline."""
    current = collect_current(formal_dir)
    BASELINE_FILE.write_text(json.dumps(current, indent=2, sort_keys=True), encoding="utf-8")
    print(f"Baseline recorded: {len(current)} models, {BASELINE_FILE}")
    print()
    print("Summary:")
    for name, r in sorted(current.items()):
        g = "✅" if r["goal_reachable"] else ("❌" if r["goal_reachable"] is False else "—")
        s = "✅" if not r["safety_violations"] else "❌"
        print(f"  {name:<25} states={r['reachable_states']:>5} goal={g} safety={s}")


def check(formal_dir):
    """Compare current to baseline. Exit non-zero on regression."""
    if not BASELINE_FILE.exists():
        print(f"❌ No baseline found at {BASELINE_FILE}")
        print(f"   Run: python baseline.py --record")
        return 2

    baseline = json.loads(BASELINE_FILE.read_text(encoding="utf-8"))
    current = collect_current(formal_dir)

    regressions = []
    additions = []

    for name in sorted(set(baseline) | set(current)):
        if name not in current:
            regressions.append(f"Model {name} disappeared from formal/")
            continue
        if name not in baseline:
            additions.append(f"New model {name} (run --record to baseline it)")
            continue

        b = baseline[name]
        c = current[name]

        # Goal regression
        if b["goal_reachable"] is True and c["goal_reachable"] is not True:
            regressions.append(f"{name}: goal NO LONGER reachable (was: ✅, now: {c['goal_reachable']})")

        # New safety violations
        new_violations = set(c["safety_violations"]) - set(b["safety_violations"])
        if new_violations:
            regressions.append(f"{name}: NEW safety violations: {sorted(new_violations)}")

        # Conservation invariant disappearance
        b_invs = {(i["expr"], i["value"]) for i in b["invariants"]}
        c_invs = {(i["expr"], i["value"]) for i in c["invariants"]}
        lost = b_invs - c_invs
        if lost:
            regressions.append(f"{name}: LOST {len(lost)} conservation invariants: {sorted(lost)[:3]}{'...' if len(lost) > 3 else ''}")

        # Drastic state-space change (>50% drop or >5x growth)
        if b["reachable_states"] > 5 and c["reachable_states"] < b["reachable_states"] * 0.5:
            regressions.append(f"{name}: reachable states dropped {b['reachable_states']} -> {c['reachable_states']} (model may have become degenerate)")
        if c["reachable_states"] > b["reachable_states"] * 5 and b["reachable_states"] > 0:
            regressions.append(f"{name}: reachable states grew {b['reachable_states']} -> {c['reachable_states']} (possible unbounded change)")

    print(f"Checked {len(current)} models against {len(baseline)} baseline entries")
    print()

    if not regressions and not additions:
        print("✅ No regressions detected. All baselined properties still hold.")
        return 0

    if additions:
        print(f"NEW models since baseline ({len(additions)}):")
        for a in additions:
            print(f"  + {a}")
        print()

    if regressions:
        print(f"❌ REGRESSIONS DETECTED ({len(regressions)}):")
        for r in regressions:
            print(f"  - {r}")
        return 1

    return 0


def diff(formal_dir):
    """Show what's different between baseline and current."""
    if not BASELINE_FILE.exists():
        print(f"No baseline. Run --record first.")
        return 0

    baseline = json.loads(BASELINE_FILE.read_text(encoding="utf-8"))
    current = collect_current(formal_dir)

    for name in sorted(set(baseline) | set(current)):
        b = baseline.get(name, {})
        c = current.get(name, {})
        if b == c:
            continue
        print(f"=== {name} ===")
        for k in sorted(set(b) | set(c)):
            if b.get(k) != c.get(k):
                print(f"  {k}: {b.get(k)} -> {c.get(k)}")
        print()
    return 0


def main():
    parser = argparse.ArgumentParser(description="Verification regression baseline")
    parser.add_argument("--record", action="store_true", help="Record current state as baseline")
    parser.add_argument("--check", action="store_true", help="Check current against baseline")
    parser.add_argument("--diff", action="store_true", help="Show baseline-vs-current diff")
    args = parser.parse_args()

    formal_dir = Path(__file__).parent.parent

    if args.record:
        return record(formal_dir) or 0
    if args.check:
        return check(formal_dir)
    if args.diff:
        return diff(formal_dir)

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())
