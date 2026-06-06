/* Handshake No-Deadlock queries */

/* Q1 — No deadlock: every non-terminal state has an enabled transition */
AG (P_A_Established + P_A_Rejected = 0 ⟹ EF (P_A_Established + P_A_Rejected = 1))

/* Q2 — Termination: both peers eventually reach a terminal state */
EF (P_A_Established = 1 AND P_B_Established = 1)
EF (P_A_Rejected = 1 AND P_B_Rejected = 1)

/* Q3 — Symmetric outcome: never one Established + other Rejected */
AG ¬ (P_A_Established = 1 AND P_B_Rejected = 1)
AG ¬ (P_A_Rejected = 1 AND P_B_Established = 1)
