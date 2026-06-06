# Pre-Key Pool Fixed — Properties

## P1 — No Exhaustion (now actually verifiable)

**Statement:** `AG (P_Pool ≥ 1)`.

**Proof:** Two transitions can decrease Pool:

1. **T_Consume_Safe** requires `P_Pool ≥ 2` (arc weight 2 on input,
   weight 1 on output). After firing: Pool decreases by 1, but only
   when it was ≥ 2 → result is ≥ 1. ✓

2. **T_Consume_LowAtomicRefill** has inhibitor arc with weight 2:
   enabled only when `P_Pool < 2` (i.e., Pool ∈ {0, 1}). Input arc
   weight 1, output arc weight 3. After firing: Pool increases by 2.
   - From Pool=1: result Pool=3 (≥1 ✓)
   - From Pool=0: inhibitor satisfied but input requires ≥1, so the
     atomic transition is also blocked → Pool never reaches 0 to fire.

**By induction:** Initial Pool=4. After any firing, Pool ∈ {3, 5, 6, …}
(never less than 3 once consume completes). The set {1, 0} is unreachable
because:
- Pool=1 is reachable only via Consume_Safe from Pool=2; once at 1,
  Consume_Safe is disabled (needs ≥2) and Consume_LowAtomicRefill
  takes over, producing 3 → Pool=3.
- Pool=0 is structurally unreachable.

`AG (P_Pool ≥ 1)` holds. ✓

## Why The Original Model Failed

The original model treated T_TriggerRefill as a permissive separate
transition. The semantic gap: in real `IPreKeyStore`, the trigger fires
**automatically** when consume crosses threshold (it's part of the
consume operation, not a separate event). The fixed model captures
that atomicity via the inhibitor arc gating the low-pool consume
into a combined consume-and-refill step.

## Mapping to Production Code

| Petri net | `IPreKeyStore` |
|---|---|
| T_Consume_Safe | `ConsumeOpkAsync()` when `_pool.Count >= 2` |
| T_Consume_LowAtomicRefill | `ConsumeOpkAsync()` with embedded `await EnsureCapacityAsync()` when low |
| Inhibitor arc | Threshold check inside `EnsureCapacityAsync` |
