# Pre-Key Pool — Fixed Model with Inhibitor Arcs

## Why a Fixed Version

The base `prekey-pool/` model has a verified bug found by `tools/verify.py`:
T_Consume can fire repeatedly without intervening T_TriggerRefill,
draining Pool from 4 to 0 and violating `AG (Pool ≥ 1)`.

This fixed model uses **inhibitor arcs** to enforce the safety property
properly. T_Consume is gated on Pool being above a threshold OR a
refill being already queued.

## Inhibitor Arc Semantics

A standard P/T net extension: an inhibitor arc from place P to transition T
means T is enabled iff `M(P) < weight(arc)` (i.e., fewer than `weight`
tokens at P).

PNML representation:
```xml
<arc id="..." source="P" target="T" type="inhibitor">
  <inscription><text>weight</text></inscription>
</arc>
```

## Model

| Transition | Condition |
|---|---|
| T_Consume_Safe | Pool ≥ 2 (normal consume) |
| T_Consume_LowAtomicRefill | Pool = 1 AND inhibitor on Pool ≥ 2: atomically consume + refill |

This atomic consume-and-refill at threshold guarantees `Pool ≥ 1`
in every reachable state.

## Files

`prekey-pool.pnml` | `properties.md` | `state-space.md`

## Verification (Once Tooling Supports Inhibitor Arcs)

The base `tools/verify.py` needs inhibitor arc support to evaluate this
model. Track in task #29 extension. For now, the structural argument
applies and is captured in `properties.md`.

For TAPAAL or LoLA verification, the inhibitor arc is natively
supported — just open the PNML and run.
