# DTN Custody Timed — Properties

## P1 — Bounded Termination (72h SLA)

**Statement:** Every bundle reaches Delivered or Expired within 72 hours
of creation.

**Formal (TCTL):**
```
AG (P_Source + P_InCustody = 1 ⟹ AF[≤259200] (P_Delivered + P_Expired = 1))
```
(259200 seconds = 72 hours)

**Proof:** The invariant on P_InCustody is `≤ 259200` (forces firing
before that time). T_Expire's firing interval is exactly [259200, 259200].
So if no T_Deliver fires within 72h, T_Expire MUST fire at 259200s. ✓

## P2 — Delivery Reachable in Realistic Time

```
EF[≤300] (P_Delivered = 1)
```
"Delivery is reachable within 5 minutes" — proves the fast path exists.

## Verification

```
verifytapn dtn-custody.tpn --query dtn-custody.q
# Both queries: SATISFIED
```

## Mapping

| TPN | Code |
|---|---|
| 259200s | `ProtocolConstants.DtnBundleTtl` (72h) |
| T_AcceptCustody [0,5] | `IDtnMeshService.AcceptCustodyAsync` ack latency |
| T_Expire | `IDtnMeshService.ExpireStaleAsync` |
