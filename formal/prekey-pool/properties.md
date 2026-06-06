# Pre-Key Pool — Properties

## ⚠ Known Model Limitation (caught by `tools/verify.py` CTL evaluator)

The current model does **not** prevent T_Consume from firing
repeatedly without an intervening refill. From initial Pool=4, the
firing sequence T_Consume × 4 reaches Pool=0 — violating P1.

This is a **model-design bug**, not a property violation in the
production code: real `IPreKeyStore` triggers refill *automatically*
when its consume operation crosses the threshold. The Petri net
above models trigger as a separate transition that can be deferred.

**Fix tracked in task #27 (inhibitor-arc cleanup)** — replace the
test-arc on T_TriggerRefill with an inhibitor arc that forces
trigger before pool drops below 1, or atomically combine consume
with auto-trigger.

The structural intent below remains correct; the model encoding
needs the Phase 2 fix.

## P1 — No Exhaustion (intended)
**Statement:** `AG (P_Pool ≥ 1)`.
**Intended proof:** `T_Consume` requires `P_Pool ≥ 1`. When pool drops to 1,
`T_TriggerRefill` (test arc) fires, then `T_Refill` produces 3 more.
The structural invariant: Pool is consumed at most one-per-firing,
refill produces 3-per-firing. So firing patterns can't drain
without refill catching up.

**Why the model doesn't yet enforce this:** trigger is permissive,
not automatic. See Phase 2 inhibitor-arc fix.

## P2 — Refill Liveness
**Statement:** Any low marking can reach a refilled marking.
**Witness:** M(pool=1) → T_TriggerRefill → M(pool=1, trigger=1) → T_Refill → M(pool=4).

## P3 — No Leak on Refill
Each refill produces exactly 3 tokens (arc weight). No transition
produces more than 3 in one firing.

## Mapping

| Petri net | `SignalProtocolMeshService.cs` |
|---|---|
| P_Pool | `_oneTimePreKeyPool.Count` |
| T_Consume | `ConsumeOneTimePreKey()` |
| T_TriggerRefill / T_Refill | `RefillOpkPool()` |
