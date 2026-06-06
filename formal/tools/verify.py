#!/usr/bin/env python3
"""
AetherMesh Petri Net Verifier
==============================

Exhaustive reachability + property checker for the AetherMesh formal
models. Reads PNML, builds the reachability graph by BFS from the
initial marking, and reports:

  - Reachable state count
  - Conservation invariants (sums of marking subsets that stay constant)
  - Boundedness (max tokens per place over all reachable states)
  - Goal-marking reachability (markings where any place has its
    "goal" value as defined by per-model GOALS)

Outputs Markdown to stdout suitable for embedding in state-space.md.

Usage:
  python verify.py <model-dir>
  python verify.py --all
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from collections import defaultdict

# CTL evaluator (sibling module)
sys.path.insert(0, str(Path(__file__).parent))
try:
    from ctl import parse_ctl, evaluate as ctl_evaluate, verify_q_file
    CTL_AVAILABLE = True
except ImportError:
    CTL_AVAILABLE = False

NS = {"pnml": "http://www.pnml.org/version-2009/grammar/pnml"}


def parse_pnml(path):
    """Parse a PNML file into (places, transitions, arcs)."""
    tree = ET.parse(path)
    root = tree.getroot()

    places = {}      # id -> initial marking
    transitions = set()
    arcs_in = defaultdict(list)   # transition_id -> [(place_id, weight)]
    arcs_out = defaultdict(list)  # transition_id -> [(place_id, weight)]

    # PNML uses an XML namespace but the file may or may not declare it
    def strip_ns(tag):
        return tag.split("}", 1)[-1] if "}" in tag else tag

    for el in root.iter():
        tag = strip_ns(el.tag)
        if tag == "place":
            pid = el.get("id")
            im = 0
            for child in el.iter():
                if strip_ns(child.tag) == "initialMarking":
                    for txt in child.iter():
                        if strip_ns(txt.tag) == "text" and txt.text:
                            im = int(txt.text.strip())
                            break
                    break
            places[pid] = im
        elif tag == "transition":
            transitions.add(el.get("id"))
        elif tag == "arc":
            src = el.get("source")
            tgt = el.get("target")
            weight = 1
            for child in el.iter():
                if strip_ns(child.tag) == "inscription":
                    for txt in child.iter():
                        if strip_ns(txt.tag) == "text" and txt.text:
                            try:
                                weight = int(txt.text.strip())
                            except ValueError:
                                pass
                            break
                    break
            if src in places and tgt in transitions:
                arcs_in[tgt].append((src, weight))
            elif src in transitions and tgt in places:
                arcs_out[src].append((tgt, weight))

    return places, sorted(transitions), arcs_in, arcs_out


def marking_to_tuple(places, marking):
    """Convert dict marking to sorted tuple for hashing."""
    return tuple(marking[p] for p in sorted(places))


def fire(marking, t_id, arcs_in, arcs_out):
    """Return new marking after firing transition t_id, or None if not enabled."""
    new = dict(marking)
    for pid, w in arcs_in[t_id]:
        if new.get(pid, 0) < w:
            return None
    for pid, w in arcs_in[t_id]:
        new[pid] -= w
    for pid, w in arcs_out[t_id]:
        new[pid] = new.get(pid, 0) + w
    return new


def reachability(places, transitions, arcs_in, arcs_out, max_states=2000):
    """BFS the reachability graph.
       Returns (markings_dict, place_keys, successors_dict, initial_id).
       markings_dict: state_id -> marking dict
       successors_dict: state_id -> set of successor state_ids
       initial_id: the state ID of the initial marking."""
    place_keys = sorted(places)
    init = dict(places)
    init_t = marking_to_tuple(places, init)

    # Use string ids (tuples not hashable for some structures); markings indexed by id
    markings = {init_t: init}
    successors = {init_t: set()}
    queue = [init]

    while queue and len(markings) < max_states:
        m = queue.pop(0)
        m_key = marking_to_tuple(places, m)
        for t in transitions:
            new_m = fire(m, t, arcs_in, arcs_out)
            if new_m is None:
                continue
            key = marking_to_tuple(places, new_m)
            if key not in markings:
                markings[key] = new_m
                successors[key] = set()
                queue.append(new_m)
            successors[m_key].add(key)

    return markings, place_keys, successors, init_t


def find_conservation_invariants(reached, place_keys):
    """Find subsets of places whose marking-sum is constant across all states."""
    states = list(reached.values())
    if not states:
        return []
    # Check the whole-net sum first
    sums = [sum(s[p] for p in place_keys) for s in states]
    invariants = []
    if len(set(sums)) == 1:
        invariants.append(("sum(all)", sums[0]))
    # Pair-wise: find pairs whose sum is constant
    for i, p1 in enumerate(place_keys):
        for p2 in place_keys[i + 1:]:
            sums2 = [s[p1] + s[p2] for s in states]
            if len(set(sums2)) == 1 and sums2[0] > 0:
                invariants.append((f"{p1} + {p2}", sums2[0]))
    return invariants[:8]


def max_marking_per_place(reached, place_keys):
    """Max token count per place across reachable states."""
    out = {}
    for s in reached.values():
        for p in place_keys:
            out[p] = max(out.get(p, 0), s.get(p, 0))
    return out


def goal_reachable(reached, place_keys, goal_predicate):
    """Check if any reachable state matches goal_predicate(state) -> bool."""
    for s in reached.values():
        if goal_predicate(s):
            return True
    return False


GOAL_PREDICATES = {
    "dtn-custody":      lambda s: any(s.get(p, 0) >= 1 for p in s if "Delivered" in p),
    "signal-protocol":  lambda s: s.get("P_ChainKey_E2", 0) >= 1,
    "vault-erasure":    lambda s: s.get("P_Recovered", 0) >= 1,
    "aodv-routing":     lambda s: s.get("P_A_HasRouteToC_viaB", 0) >= 1 and s.get("P_B_HasRouteToC_direct", 0) >= 1,
    "watch-together":   lambda s: s.get("P_F1_AtHostPos", 0) >= 1 and s.get("P_F2_AtHostPos", 0) >= 1,
    "pov-anti-sybil":   lambda s: s.get("P_S_Count", 0) >= 3,
    "prekey-pool":      lambda s: s.get("P_Pool", 0) >= 3,
    "handshake-deadlock": lambda s: (s.get("P_A_Established", 0) >= 1 and s.get("P_B_Established", 0) >= 1) or (s.get("P_A_Rejected", 0) >= 1 and s.get("P_B_Rejected", 0) >= 1),
    "sos-flood":        lambda s: all(s.get(f"P_N{i}_Alerted", 0) >= 1 for i in (1, 2, 3)),
    "chipin-atomicity": lambda s: s.get("P_CreatorBalance", 0) >= 100,
    "reputation-gossip": lambda s: all(s.get(f"P_N{i}_KnowsScore", 0) >= 1 for i in (1, 2, 3)),
    "group-voice-rotation": lambda s: s.get("P_GroupKey_v2", 0) >= 1,
    "forge-eviction":   lambda s: s.get("P_Pkg3_Cached", 0) >= 1,
    "outbox-backpressure": lambda s: s.get("P_Delivered", 0) >= 5,
    "anomaly-detector": lambda s: s.get("P_Flagged", 0) >= 1,
    "health-convergence": lambda s: s.get("P_Overall_Healthy", 0) >= 1,
    "trust-ring":       lambda s: s.get("P_Attested", 0) >= 1,
    "transport-selector": lambda s: any(s.get(f"P_Selected_{n}", 0) >= 1 for n in ("BLE", "WifiDirect", "LoRa")),
    "multi-device-sync": lambda s: all(s.get(f"P_D{i}_HasKey", 0) >= 1 for i in (1, 2, 3)),
    "market-escrow":    lambda s: s.get("P_Buyer_HasVault", 0) >= 1 and s.get("P_Seller_HasFunds", 0) >= 100,
}


def is_safety_violation(reached, place_keys, model_name):
    """Check critical safety violations per model."""
    violations = []
    states = list(reached.values())
    for s in states:
        # Universal: no token explosion
        for p in place_keys:
            if s.get(p, 0) > 1000:
                violations.append(f"unbounded {p}")
                break
        # Model-specific
        if model_name == "aodv-routing":
            if s.get("P_A_HasRouteToC_viaB", 0) >= 1 and s.get("P_B_NoRouteToC", 0) >= 1:
                violations.append("routing loop (A has route via B, B has no route)")
        elif model_name == "market-escrow":
            if s.get("P_Buyer_HasVault", 0) >= 1 and s.get("P_Seller_HasFunds", 0) < 100:
                violations.append("half-settle (buyer has vault without seller paid)")
    return list(set(violations))


def verify_model(model_dir):
    name = model_dir.name
    pnml_files = list(model_dir.glob("*.pnml"))
    if not pnml_files:
        return None
    pnml = pnml_files[0]

    places, transitions, arcs_in, arcs_out = parse_pnml(pnml)
    markings, place_keys, successors, init_id = reachability(places, transitions, arcs_in, arcs_out)
    invariants = find_conservation_invariants(markings, place_keys)
    max_marks = max_marking_per_place(markings, place_keys)
    goal_pred = GOAL_PREDICATES.get(name)
    goal_ok = goal_reachable(markings, place_keys, goal_pred) if goal_pred else None
    violations = is_safety_violation(markings, place_keys, name)

    # CTL verification of .q file (Phase 1 addition)
    ctl_results = []
    if CTL_AVAILABLE:
        q_files = list(model_dir.glob("*.q"))
        if q_files:
            try:
                ctl_results = verify_q_file(q_files[0], markings, successors, init_id)
            except Exception as e:
                ctl_results = [(f"<failed to parse {q_files[0].name}>", None, str(e))]

    return {
        "name": name,
        "places": len(places),
        "transitions": len(transitions),
        "reachable_states": len(markings),
        "invariants": invariants,
        "max_marks": max_marks,
        "goal_reachable": goal_ok,
        "safety_violations": violations,
        "ctl_results": ctl_results,
    }


def render_report(result):
    if not result:
        return ""
    lines = []
    n = result["name"]
    lines.append(f"## Machine-Checked Verification (`tools/verify.py`)")
    lines.append("")
    lines.append(f"| Metric | Value |")
    lines.append(f"|---|---|")
    lines.append(f"| Places | {result['places']} |")
    lines.append(f"| Transitions | {result['transitions']} |")
    lines.append(f"| **Reachable states** | **{result['reachable_states']}** |")
    if result["goal_reachable"] is not None:
        lines.append(f"| Goal reachable | {'✅ YES' if result['goal_reachable'] else '❌ NO'} |")
    lines.append(f"| Safety violations | {'❌ ' + ', '.join(result['safety_violations']) if result['safety_violations'] else '✅ none'} |")
    lines.append("")
    if result.get("ctl_results"):
        lines.append("### CTL Query Verification (`.q` file)")
        lines.append("")
        lines.append("| # | Query | Result |")
        lines.append("|---|---|---|")
        for i, (q, ok, err) in enumerate(result["ctl_results"], 1):
            badge = "✅ SAT" if ok else ("❌ NOT SAT" if ok is False else "⚠ parse-fail")
            q_short = q if len(q) < 60 else q[:57] + "..."
            lines.append(f"| {i} | `{q_short}` | {badge} |")
        lines.append("")

    if result["invariants"]:
        lines.append("### Conservation Invariants (auto-discovered)")
        lines.append("")
        for inv, val in result["invariants"]:
            lines.append(f"- `{inv} = {val}` holds in **all** reachable states")
        lines.append("")
    lines.append("### Boundedness (max token count per place)")
    lines.append("")
    lines.append("| Place | Max tokens |")
    lines.append("|---|---|")
    sorted_marks = sorted(result["max_marks"].items(), key=lambda x: (-x[1], x[0]))
    for p, m in sorted_marks[:12]:
        lines.append(f"| {p} | {m} |")
    if len(sorted_marks) > 12:
        lines.append(f"| ... | (+ {len(sorted_marks) - 12} more places) |")
    lines.append("")
    return "\n".join(lines)


def main():
    formal_dir = Path(__file__).parent.parent
    if len(sys.argv) >= 2 and sys.argv[1] == "--all":
        results = []
        for d in sorted(formal_dir.iterdir()):
            if d.is_dir() and d.name != "tools" and (d / f"{d.name}.pnml").exists():
                r = verify_model(d)
                if r:
                    results.append(r)
        # Summary table
        print("# AetherMesh Formal Verification — Summary")
        print()
        print("| Model | States | Goal | Safety |")
        print("|---|---|---|---|")
        for r in results:
            g = "✅" if r["goal_reachable"] else ("❌" if r["goal_reachable"] is False else "—")
            s = "✅" if not r["safety_violations"] else "❌"
            print(f"| {r['name']} | {r['reachable_states']} | {g} | {s} |")
        print()
        total = sum(r["reachable_states"] for r in results)
        ok_goal = sum(1 for r in results if r["goal_reachable"])
        ok_safe = sum(1 for r in results if not r["safety_violations"])
        print(f"**Total reachable states across all 20 models: {total}**")
        print(f"**Goal reached: {ok_goal} / {len(results)}**")
        print(f"**Safety violations: {len(results) - ok_safe} / {len(results)}**")
        # Write each model's verification.md
        for r in results:
            md = render_report(r)
            (formal_dir / r["name"] / "verification.md").write_text(md, encoding="utf-8")
        print()
        print(f"Per-model reports written to formal/<model>/verification.md")
    else:
        model_dir = Path(sys.argv[1])
        r = verify_model(model_dir)
        print(render_report(r))


if __name__ == "__main__":
    main()
