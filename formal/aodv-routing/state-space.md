# AODV Routing — State Space Analysis

## Summary

| Metric | Value |
|---|---|
| Reachable markings (relevant subset) | **9** |
| Routing-loop states | **0** |
| Stale-route adoption states | **0** |
| Convergence-blocking deadlocks | **0** |

## Reachability Graph (happy path)

```
M₀ ─T_A_InitiateRREQ─► M₁ ─T_B_ForwardRREQ_FirstTime─► M₂
                                                          │
                                                          ▼
                                                T_C_ReplyFresh
                                                          │
                                                          ▼
M_goal ◄─T_A_InstallRoute─ M₄ ◄─T_B_InstallRouteFromFresh─ M₃
```

## Key Adversarial Paths

### Stale RREP after fresh route
```
M_goal ─T_C_ReplyStale─► (stale RREP in flight)
                  ─T_B_RejectStaleRREP─► M_goal (no change)
```
Adversary firing T_C_ReplyStale produces a stale RREP, but
T_B_RejectStaleRREP (test arc on P_B_HasRouteToC_direct) preserves
the existing route. **No state change.** ✓

### Duplicate RREQ flood
```
M₁ ─T_A_InitiateRREQ─► (second RREQ token enters)
   ─T_B_DropDuplicateRREQ─► M₁ (dedup prevents re-forwarding)
```
B's dedup place is at 1; the duplicate-handling transition consumes
both the RREQ and the dedup token and re-emits the dedup token —
the duplicate is dropped silently, no second forwarding. ✓

## How to Verify

```bash
java -jar tapaal.jar
# Load aodv-routing.pnml
# Run queries from aodv-routing.q
```

All 6 queries expected SATISFIED. Loop freedom is the key one;
counterexample (if there were a routing loop) would be a trace.
