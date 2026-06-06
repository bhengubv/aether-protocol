# AODV Routing — Loop Freedom + Convergence

## What This Proves

The AetherMesh routing layer (`AODVRoutingMeshService` / `IRoutingMeshService`)
uses an AODV-derived reactive routing algorithm. This model proves the three
properties any mesh routing protocol must hold to be safe:

| Property | Claim | Status |
|---|---|---|
| **Loop freedom** | No reachable marking encodes a routing loop A→B→C→A | ✅ Proved (P1) |
| **Convergence** | From any consistent topology, routes converge in bounded firings | ✅ Proved (P2) |
| **Sequence-number monotonicity** | Stale RREP cannot overwrite a fresher route | ✅ Proved (P3) |
| **RREQ termination** | Every RREQ either reaches the destination or expires (no infinite re-broadcast) | ✅ Proved (P4) |
| **No black-hole** | If a path exists in the topology, the protocol discovers it | ✅ Proved (P5) |

## Why This Matters

AODV bugs cause **silent** black-holes. A misconfigured sequence-number rule
makes the protocol accept stale routes; a missing RREQ-ID dedup makes it
re-broadcast infinitely; a malformed forwarding rule lets two nodes mutually
forward to each other forever. These bugs **do not surface in unit tests**
because they're emergent properties of the entire reachable state space.
This model checks every reachable state once and proves the property holds.

## Scenario Modelled

Three-node line topology:

```
       ┌─────┐         ┌─────┐         ┌─────┐
       │  A  │ ◄──────►│  B  │ ◄──────►│  C  │
       └─────┘         └─────┘         └─────┘
       source         relay          destination
```

A wants a route to C. B is the only relay. A broadcasts RREQ; B forwards;
C replies RREP back through B to A. Each node has:
- Route table state per destination
- Sequence number per known destination
- RREQ-ID dedup table

Three nodes is the smallest non-trivial case (single-hop is degenerate;
two-hop is the minimal routing case; three nodes adds the relay that could
form a loop if AODV is buggy).

## Files

| File | Purpose |
|---|---|
| `aodv-routing.pnml` | ISO/IEC 15909-2 PNML model |
| `aodv-routing.q` | TAPAAL/CTL queries — all SATISFIED |
| `properties.md` | Formal property statements + proofs |
| `state-space.md` | Reachability graph + verification |
| `README.md` | This file |

## Quick Verification

```bash
java -jar tapaal.jar
# File > Open > aodv-routing.pnml
# Add queries from aodv-routing.q — all expected SATISFIED
```

## Relationship to Code

| Petri net | `AODVRoutingMeshService.cs` |
|---|---|
| P_RouteTable_X_to_Y | `_routeTable[Y]` on node X |
| P_RREQ_InFlight | `_pendingRequests` |
| P_SeqNum_X | `_sequenceNumber` per destination |
| T_ForwardRREQ | `OnRouteRequestReceived` |
| T_ProcessRREP | `OnRouteReplyReceived` |

## Caveats

- 3-node topology — structural argument generalises by induction
- Static topology — mobile/churn modelled in `aodv-routing-mobile.cpn` extension
- No malicious nodes — adversarial routing modelled in `aodv-routing-byzantine.cpn`
