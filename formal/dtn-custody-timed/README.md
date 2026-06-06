# DTN Custody — Timed Petri Net (72h custody window)

## What This Adds

Proves the production AetherMesh DTN custody window — a bundle either
delivers or expires **within 72 hours of creation**, never lost in
limbo. The base P/T net proved "eventually delivers or expires"; the
timed net proves "**within 72h** delivers or expires."

## Timed Semantics

| Transition | Delay | Meaning |
|---|---|---|
| T_AcceptCustody | [0.1, 5] s | Custody handshake one-hop |
| T_DeliverFromCustody | [0, 1] s | Delivery to destination (when in range) |
| T_TtlExpire | exactly 72h | TTL timeout (configurable via `ProtocolConstants.DtnBundleTtl`) |
| T_ForwardCustody | [0.5, 60] s | Inter-relay handoff |

## Property

```
AG (Bundle_Created ⟹ AF[≤72h] (Delivered ∨ Expired))
```

Every bundle reaches a terminal state within 72 hours.

## Files

- `dtn-custody.tpn` — TAPAAL TPN format
- `properties.md` — TCTL queries + bounded-time proofs

## Maps To

`AetherMesh.Core.Dtn.DtnMeshService` — `IDtnService.MaxCustodyDuration`
constant (`ProtocolConstants.DtnBundleTtl = 72h`).
