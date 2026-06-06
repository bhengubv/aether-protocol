/* PoV Anti-Sybil queries */

/* Q1 — No double-vouching: each witness vouches at most once */
AG (P_W1_Vouched <= 1 AND P_W2_Vouched <= 1 AND P_W3_Vouched <= 1)

/* Q2 — No Sybil amplification: witness count ≤ number of vouched witnesses */
AG (P_S_Count = P_W1_Vouched + P_W2_Vouched + P_W3_Vouched)

/* Q3 — Witness count is bounded by witness pool size */
AG (P_S_Count <= 3)

/* Q4 — Defection cascade: fraud flag enables penalty for every voucher */
AG (P_S_FraudFlagged = 1 AND P_W1_Vouched = 1 ⟹ EF P_W1_Penalty = 1)

/* Q5 — Maximum witness count (here 3) is reachable through legit vouches only */
EF (P_S_Count = 3)
