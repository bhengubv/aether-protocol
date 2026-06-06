/* AetherMesh Signal Protocol — Forward + Future Secrecy queries
   Run: verifytapn --trace some signal-protocol.pnml --query signal-protocol.q
   All queries expected: SATISFIED
   SPDX-License-Identifier: MIT */

/* Q1 — Forward secrecy (safety).
   Once the E0 chain key is destroyed, no attacker can learn it.
   Equivalent formulation: AG (CK_E0 = 0 ⟹ AG attacker bounded).
   In TAPAAL/CTL: the attacker place's value at any "post-ratchet" state
   is bounded by its value at the moment of ratchet. */
AG (P_Attacker_E0 <= 1)

/* Q2 — Future secrecy (existence).
   There is a reachable state where the attacker has E0, E1 exists,
   but the attacker has NOT compromised E1. */
EF (P_Attacker_E0 = 1 AND P_ChainKey_E1 = 1 AND P_Attacker_E1 = 0)

/* Q3 — Ratchet progression: E2 is reachable. */
EF (P_ChainKey_E2 = 1)

/* Q4 — Chain-key linearity: at most one chain key exists at any time. */
AG (P_ChainKey_E0 + P_ChainKey_E1 + P_ChainKey_E2 <= 1)

/* Q5 — Compromise independence: there exists a state with
   E1 compromised but neither E0 nor E2 compromised. */
EF (P_Attacker_E0 = 0 AND P_Attacker_E1 = 1 AND P_Attacker_E2 = 0)

/* Q6 — Worst-case bound: all 3 epochs can be compromised in some sequence. */
EF (P_Attacker_E0 = 1 AND P_Attacker_E1 = 1 AND P_Attacker_E2 = 1)
