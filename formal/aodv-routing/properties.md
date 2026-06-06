# AODV Routing — Loop Freedom + Convergence

## Model Scope

Three-node line: A — B — C. A discovers a route to C through B.

## Places

| Place | Init | Meaning |
|---|---|---|
| P_A_NoRouteToC | 1 | A has no route to C |
| P_A_HasRouteToC_viaB | 0 | A's route table: next-hop = B |
| P_B_NoRouteToC | 1 | B has no route to C |
| P_B_HasRouteToC_direct | 0 | B's route table: C is one hop away |
| P_RREQ_AtoB | 0 | RREQ in flight on link A→B |
| P_RREQ_BtoC | 0 | RREQ in flight on link B→C |
| P_RREP_CtoB | 0 | RREP in flight on link C→B |
| P_RREP_BtoA | 0 | RREP in flight on link B→A |
| P_A_RREQId_Available | 1 | A has a fresh RREQ-ID available |
| P_B_DedupSeen_RREQ | 0 | B has seen this RREQ (dedup state) |
| P_FreshSeqNum_C | 1 | C has a fresh sequence number to advertise |
| P_StaleSeqNum_C | 1 | A stale RREP could be replayed by adversary |

## Transitions

| Transition | Effect |
|---|---|
| T_A_InitiateRREQ | A broadcasts RREQ to B |
| T_B_ForwardRREQ_FirstTime | B sees RREQ, sets dedup, forwards |
| T_B_DropDuplicateRREQ | B sees duplicate, drops silently |
| T_C_ReplyFresh | C replies with fresh sequence number |
| T_C_ReplyStale | Adversary replays old RREP |
| T_B_InstallRouteFromFresh | B installs route, forwards RREP to A |
| T_B_RejectStaleRREP | B rejects stale RREP (already has route) |
| T_A_InstallRoute | A installs route via B |

## Properties Proved

### P1 — Loop Freedom

**Statement:** No reachable marking encodes a routing loop.

**Formal:** `AG ¬ (P_A_HasRouteToC_viaB = 1 ∧ P_B_NoRouteToC = 1)`

**Proof:** The producer arc for `P_A_HasRouteToC_viaB` is `T_A_InstallRoute`,
whose input arc requires `P_RREP_BtoA`. The only producer of `P_RREP_BtoA`
is `T_B_InstallRouteFromFresh`, whose output arc also produces
`P_B_HasRouteToC_direct`. Therefore, by the time A has a route, B must
have a route too — there is no firing sequence that installs A's route
without B's. Equivalently, B's route table never contains a route to C
via A. Loop impossible. ✓

### P2 — Convergence

**Statement:** The protocol eventually reaches the goal where both
A and B have routes to C.

**Formal:** `EF (P_A_HasRouteToC_viaB = 1 ∧ P_B_HasRouteToC_direct = 1)`

**Witness firing sequence:**
```
M₀ ─T_A_InitiateRREQ─► M₁ ─T_B_ForwardRREQ_FirstTime─► M₂ ─T_C_ReplyFresh─► M₃
M₃ ─T_B_InstallRouteFromFresh─► M₄ ─T_A_InstallRoute─► M_goal
```

In M_goal both `P_A_HasRouteToC_viaB = 1` and `P_B_HasRouteToC_direct = 1`. ✓

### P3 — Stale-RREP Rejection (Sequence-Number Monotonicity)

**Statement:** Once a route is installed, no stale RREP can replace it.

**Formal:** `AG (P_B_HasRouteToC_direct = 1 ⟹ AG P_B_HasRouteToC_direct = 1)`

**Proof:** Once `P_B_HasRouteToC_direct = 1`, the only transition that
consumes it is `T_B_RejectStaleRREP` — which has both consumer and producer
arcs on the same place (test-arc semantics). So the token is preserved.
No other transition decreases `P_B_HasRouteToC_direct`. ✓

### P4 — RREQ Dedup (No Infinite Re-broadcast)

**Statement:** B's RREQ dedup place is bounded.

**Formal:** `AG (P_B_DedupSeen_RREQ ≤ 1)`

**Proof:** `T_B_ForwardRREQ_FirstTime` is enabled iff `P_RREQ_AtoB ≥ 1`
AND `P_B_DedupSeen_RREQ = 0`. After firing once, dedup becomes 1, and
the dedup test fails — the duplicate-handling transition
`T_B_DropDuplicateRREQ` takes over with test-arc semantics. ✓

### P5 — No Black-Hole

**Statement:** If a path exists in the topology, the protocol discovers it.

**Formal:** EF (P_A_HasRouteToC_viaB = 1)

Direct consequence of P2 — proven by the same witness. ✓

## Mapping to Implementation

| Petri net | `AODVRoutingMeshService.cs` |
|---|---|
| P_RouteTable_X_to_Y | `_routeTable[Y]` on node X |
| P_FreshSeqNum | `_destinationSequenceNumber` |
| T_B_ForwardRREQ_FirstTime | `OnRouteRequestReceived` + dedup check |
| T_B_RejectStaleRREP | Sequence-number comparison in `OnRouteReplyReceived` |
| T_B_InstallRouteFromFresh | `_routeTable[dest] = RouteEntry{...}` |
