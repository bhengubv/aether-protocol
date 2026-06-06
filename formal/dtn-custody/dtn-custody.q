/* AetherMesh DTN Custody — TAPAAL / CTL verification queries
   Run: verifytapn --trace some dtn-custody.pnml --query dtn-custody.q
   All queries expected: SATISFIED
   SPDX-License-Identifier: MIT */

/* Q1 — Bundle conservation (safety invariant).
   The sum of bundle-carrying places always equals 1.
   Violation would mean a bundle was created from nothing or silently dropped. */
AG (P_Source + P_Relay + P_Delivered + P_Expired = 1)

/* Q2 — No deadlock.
   Every state can fire at least one transition (EX true = next state exists). */
AG (EX true)

/* Q3 — Delivery is reachable from the initial state (happy path). */
EF (P_Delivered = 1)

/* Q4 — Self-healing: from any state where Source holds the bundle
   AND the relay is down, delivery is still eventually reachable. */
AG ((P_Source = 1 AND P_RelayDown = 1) => EF (P_Delivered = 1))

/* Q5 — Expiry is reachable (bundles always have a terminal path). */
EF (P_Expired = 1)

/* Q6 — Relay state invariant: relay is always in exactly one state (up XOR down). */
AG (P_RelayUp + P_RelayDown = 1)
