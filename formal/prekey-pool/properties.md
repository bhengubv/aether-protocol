# Pre-Key Pool — Properties

## P1 — No Exhaustion
**Statement:** `AG (P_Pool ≥ 1)`.
**Proof:** `T_Consume` requires `P_Pool ≥ 1`. When pool drops to 1,
`T_TriggerRefill` (test arc) fires, then `T_Refill` produces 3 more.
The structural invariant: Pool is consumed at most one-per-firing,
refill produces 3-per-firing. So firing patterns can't drain
without refill catching up.

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
