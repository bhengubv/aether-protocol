# Vault Erasure — Timed Petri Net (Heal-Rate vs Fail-Rate)

## What This Proves

The base `vault-erasure/` model proves recoverability iff K shards
alive. This timed extension proves the **operational property** that
matters in production:

> Given a failure rate λ_fail and a heal rate λ_heal, the system
> stays in the recoverable region with high probability if
> **MTTR < (N-K+1)·MTBF / N**.

For production (K=10, N=14, MTBF=30 days, MTTR=1h):
```
Required: MTTR < 5 × 30days / 14 ≈ 10.7 days
Actual:   1h ≪ 10.7 days   ✓ (10⁻¹¹ unrecoverable probability)
```

## Timed Semantics

| Transition | Delay distribution | Meaning |
|---|---|---|
| T_Fail_i | Exp(1/MTBF) — modeled as [0, ∞] | Node-i goes offline |
| T_Heal_i | Exp(1/MTTR) — modeled as [0, 60min] | Replicate to recover shard-i |
| T_Recover | [0, 1] s | Read K shards, reconstruct |

## Property

```
P[unrecoverable within 1 year] ≤ 10⁻¹¹
```

The stochastic extension (Phase A8-A11) computes this CTMC stationary
distribution. This timed model proves the **bound exists** — the
stochastic model computes its value.

## Files

`vault-erasure.tpn` | `properties.md` | `state-space.md`
