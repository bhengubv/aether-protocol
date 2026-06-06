# Vault Timed — Properties

## P1 — Heal-Faster-Than-Fail Invariant

When MTTR < MTBF/N, the system never spends >ε of its time in the
unrecoverable region.

Formal (TCTL):
```
AG [number of HasShard places ≥ 2 within bounded window]
```

The base TPN proves the structural existence of bounded-time heal
paths. The stochastic upgrade (A8-A11) computes actual probabilities.

## P2 — Recovery-Window Bound

```
EF[≤MTTR] (P_HasShard_i for any i lost can be restored)
```

Within MTTR (1h in production), any single failed shard can be healed.
This bounds the "vulnerable" period during which a second failure
would tip into unrecoverable.

## Verification

Run TAPAAL on `vault-erasure.tpn`. The bound is structurally derivable
from the firing intervals.
