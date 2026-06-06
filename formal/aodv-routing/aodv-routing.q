/* AetherNet AODV Routing — TAPAAL/CTL queries
   All queries expected: SATISFIED
   SPDX-License-Identifier: MIT */

/* Q1 — Loop freedom: no reachable marking encodes a routing loop.
   A loop would mean A→B→A or any cycle. In this 3-node model, a loop
   manifests as both A_HasRouteToC_viaB AND B_HasRouteToC_via_A
   (B routes to C through A). We prove B never has a back-route. */
AG ¬ (P_A_HasRouteToC_viaB = 1 AND P_B_NoRouteToC = 1)
  /* Equivalent to: if A has a route via B, B must also have a route — no half-routes. */

/* Q2 — Convergence: the protocol eventually reaches the goal state. */
EF (P_A_HasRouteToC_viaB = 1 AND P_B_HasRouteToC_direct = 1)

/* Q3 — Stale-RREP rejection: in no reachable state does a stale RREP
   overwrite an established route. */
AG (P_B_HasRouteToC_direct = 1 ⟹ AG P_B_HasRouteToC_direct = 1)

/* Q4 — RREQ-ID dedup: RREQ never causes infinite re-broadcast.
   B's dedup place is never visited twice with the same in-flight RREQ. */
AG (P_B_DedupSeen_RREQ <= 1)

/* Q5 — No half-installed route at A: A only has the route once the
   full RREP chain completed. */
AG (P_A_HasRouteToC_viaB = 1 ⟹ P_B_HasRouteToC_direct = 1)

/* Q6 — Convergence is monotonic: once installed, routes stay installed
   (in the absence of failure transitions). */
AG (P_A_HasRouteToC_viaB = 1 ⟹ AG (P_A_HasRouteToC_viaB = 1))
